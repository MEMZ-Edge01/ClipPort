#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "native_copy.h"

#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <new>
#include <system_error>
#include <thread>
#include <vector>

namespace
{
    constexpr DWORD kBufferSize = 4u * 1024u * 1024u;
    constexpr std::size_t kBufferCount = 4;
    constexpr ULONGLONG kDirectIoThreshold = 32ull * 1024ull * 1024ull;

    struct BufferSlot
    {
        BYTE* data = nullptr;
        DWORD validBytes = 0;
        ULONGLONG offset = 0;
        bool ready = false;
    };

    ULONGLONG AlignDown(ULONGLONG value, DWORD alignment)
    {
        return alignment == 0 ? value : value - (value % alignment);
    }

    DWORD GetSectorSize(const wchar_t* path)
    {
        wchar_t volumePath[MAX_PATH]{};
        if (!GetVolumePathNameW(path, volumePath, ARRAYSIZE(volumePath)))
        {
            // Cannot resolve volume path — fall back to buffered I/O.
            return 0;
        }

        DWORD sectorsPerCluster = 0;
        DWORD bytesPerSector = 0;
        DWORD freeClusters = 0;
        DWORD totalClusters = 0;
        // A zero return disables Direct I/O for this volume, which is the
        // safe default when the filesystem doesn't expose sector information.
        return GetDiskFreeSpaceW(
            volumePath, &sectorsPerCluster, &bytesPerSector,
            &freeClusters, &totalClusters)
            ? bytesPerSector
            : 0;
    }

    class NativeCopyOperation
    {
    public:
        NativeCopyOperation()
            : cancelEvent_(CreateEventW(nullptr, TRUE, FALSE, nullptr))
        {
        }

        ~NativeCopyOperation()
        {
            Cancel();
            CloseFiles();
            ReleaseBuffers();
            if (cancelEvent_)
            {
                CloseHandle(cancelEvent_);
            }
        }

        bool IsValid() const
        {
            return cancelEvent_ != nullptr;
        }

        void Cancel()
        {
            cancelled_.store(true, std::memory_order_release);
            if (cancelEvent_)
            {
                SetEvent(cancelEvent_);
            }
            {
                std::lock_guard<std::mutex> lock(handleMutex_);
                if (source_ != INVALID_HANDLE_VALUE)
                {
                    CancelIoEx(source_, nullptr);
                }
                if (destination_ != INVALID_HANDLE_VALUE)
                {
                    CancelIoEx(destination_, nullptr);
                }
            }
            freeCondition_.notify_all();
            readyCondition_.notify_all();
        }
        DWORD CopyFile(
            const wchar_t* sourcePath,
            const wchar_t* destinationPath,
            DWORD flags,
            EzditNativeCopyProgressCallback progressCallback,
            void* progressContext)
        {
            if (!sourcePath || !destinationPath || sourcePath[0] == L'\0' ||
                destinationPath[0] == L'\0')
            {
                return ERROR_INVALID_PARAMETER;
            }

            bool expected = false;
            if (!running_.compare_exchange_strong(expected, true))
            {
                return ERROR_BUSY;
            }
            struct RunningGuard
            {
                std::atomic<bool>& value;
                ~RunningGuard()
                {
                    value.store(false, std::memory_order_release);
                }
            } runningGuard{running_};

            if (cancelled_.load(std::memory_order_acquire))
            {
                return ERROR_OPERATION_ABORTED;
            }

            WIN32_FILE_ATTRIBUTE_DATA attributes{};
            if (!GetFileAttributesExW(
                    sourcePath, GetFileExInfoStandard, &attributes))
            {
                return GetLastError();
            }
            if ((attributes.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
            {
                return ERROR_DIRECTORY;
            }

            fileSize_ =
                (static_cast<ULONGLONG>(attributes.nFileSizeHigh) << 32) |
                attributes.nFileSizeLow;
            alignment_ = std::max(
                GetSectorSize(sourcePath), GetSectorSize(destinationPath));
            directIo_ =
                (flags & EZDIT_NATIVE_COPY_FLAG_DIRECT_IO) != 0 &&
                fileSize_ >= kDirectIoThreshold &&
                alignment_ != 0 &&
                alignment_ <= kBufferSize &&
                (kBufferSize % alignment_) == 0;
            pipelineLength_ =
                directIo_ ? AlignDown(fileSize_, alignment_) : fileSize_;
            DWORD error = OpenFiles(
                sourcePath, destinationPath, directIo_, true);
            if (error != ERROR_SUCCESS && directIo_)
            {
                CloseFiles();
                directIo_ = false;
                alignment_ = 0;
                pipelineLength_ = fileSize_;
                error = OpenFiles(
                    sourcePath, destinationPath, false, true);
            }
            if (error != ERROR_SUCCESS)
            {
                return error;
            }
            if (cancelled_.load(std::memory_order_acquire))
            {
                CloseFiles();
                return ERROR_OPERATION_ABORTED;
            }

            error = AllocateBuffers();
            if (error != ERROR_SUCCESS)
            {
                CloseFiles();
                return error;
            }

            progressCallback_ = progressCallback;
            progressContext_ = progressContext;
            firstError_.store(ERROR_SUCCESS, std::memory_order_release);

            std::thread reader;
            std::thread writer;
            try
            {
                reader = std::thread(
                    &NativeCopyOperation::ReaderLoop, this);
                writer = std::thread(
                    &NativeCopyOperation::WriterLoop, this);
            }
            catch (const std::system_error&)
            {
                SetError(ERROR_NOT_ENOUGH_MEMORY);
            }
            catch (const std::bad_alloc&)
            {
                SetError(ERROR_NOT_ENOUGH_MEMORY);
            }
            if (reader.joinable())
            {
                reader.join();
            }
            if (writer.joinable())
            {
                writer.join();
            }

            error = firstError_.load(std::memory_order_acquire);
            if (error == ERROR_SUCCESS &&
                cancelled_.load(std::memory_order_acquire))
            {
                error = ERROR_OPERATION_ABORTED;
            }
            if (error == ERROR_SUCCESS && !FlushFileBuffers(destination_))
            {
                error = GetLastError();
            }
            if (error == ERROR_SUCCESS && pipelineLength_ < fileSize_)
            {
                error = CopyBufferedTail(
                    sourcePath, destinationPath, pipelineLength_,
                    static_cast<DWORD>(fileSize_ - pipelineLength_));
            }

            ReleaseBuffers();
            CloseFiles();
            return error == ERROR_SUCCESS &&
                   cancelled_.load(std::memory_order_acquire)
                ? ERROR_OPERATION_ABORTED
                : error;
        }

    private:
        DWORD OpenFiles(
            const wchar_t* sourcePath,
            const wchar_t* destinationPath,
            bool directIo,
            bool createDestination)
        {
            DWORD commonFlags = FILE_ATTRIBUTE_NORMAL |
                FILE_FLAG_SEQUENTIAL_SCAN | FILE_FLAG_OVERLAPPED;
            if (directIo)
            {
                commonFlags |= FILE_FLAG_NO_BUFFERING;
            }

            HANDLE source = CreateFileW(
                sourcePath, GENERIC_READ, FILE_SHARE_READ, nullptr,
                OPEN_EXISTING, commonFlags, nullptr);
            if (source == INVALID_HANDLE_VALUE)
            {
                return GetLastError();
            }

            HANDLE destination = CreateFileW(
                destinationPath, GENERIC_WRITE, 0, nullptr,
                createDestination ? CREATE_ALWAYS : OPEN_EXISTING,
                commonFlags, nullptr);
            if (destination == INVALID_HANDLE_VALUE)
            {
                DWORD openError = GetLastError();
                CloseHandle(source);
                return openError;
            }

            {
                std::lock_guard<std::mutex> lock(handleMutex_);
                source_ = source;
                destination_ = destination;
            }
            if (cancelled_.load(std::memory_order_acquire))
            {
                Cancel();
                return ERROR_OPERATION_ABORTED;
            }
            return ERROR_SUCCESS;
        }

        void CloseFiles()
        {
            std::lock_guard<std::mutex> lock(handleMutex_);
            if (source_ != INVALID_HANDLE_VALUE)
            {
                CloseHandle(source_);
                source_ = INVALID_HANDLE_VALUE;
            }
            if (destination_ != INVALID_HANDLE_VALUE)
            {
                CloseHandle(destination_);
                destination_ = INVALID_HANDLE_VALUE;
            }
        }
        DWORD AllocateBuffers()
        {
            try
            {
                slots_.resize(kBufferCount);
            }
            catch (const std::bad_alloc&)
            {
                return ERROR_NOT_ENOUGH_MEMORY;
            }

            for (BufferSlot& slot : slots_)
            {
                slot.data = static_cast<BYTE*>(VirtualAlloc(
                    nullptr, kBufferSize, MEM_RESERVE | MEM_COMMIT,
                    PAGE_READWRITE));
                if (!slot.data)
                {
                    ReleaseBuffers();
                    return ERROR_NOT_ENOUGH_MEMORY;
                }
            }
            return ERROR_SUCCESS;
        }

        void ReleaseBuffers()
        {
            for (BufferSlot& slot : slots_)
            {
                if (slot.data)
                {
                    VirtualFree(slot.data, 0, MEM_RELEASE);
                    slot.data = nullptr;
                }
                slot.ready = false;
                slot.validBytes = 0;
                slot.offset = 0;
            }
            slots_.clear();
        }

        DWORD TransferAt(
            bool write,
            HANDLE file,
            BYTE* buffer,
            DWORD bytesRequested,
            ULONGLONG offset,
            DWORD& bytesTransferred)
        {
            bytesTransferred = 0;
            HANDLE ioEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!ioEvent)
            {
                return GetLastError();
            }

            OVERLAPPED overlapped{};
            overlapped.Offset = static_cast<DWORD>(offset & 0xffffffffull);
            overlapped.OffsetHigh = static_cast<DWORD>(offset >> 32);
            overlapped.hEvent = ioEvent;

            BOOL started = write
                ? WriteFile(file, buffer, bytesRequested, nullptr, &overlapped)
                : ReadFile(file, buffer, bytesRequested, nullptr, &overlapped);
            if (!started)
            {
                DWORD error = GetLastError();
                if (error != ERROR_IO_PENDING)
                {
                    CloseHandle(ioEvent);
                    return error;
                }

                HANDLE waitHandles[] = {ioEvent, cancelEvent_};
                DWORD waitResult = WaitForMultipleObjects(
                    ARRAYSIZE(waitHandles), waitHandles, FALSE, INFINITE);
                if (waitResult == WAIT_OBJECT_0 + 1)
                {
                    CancelIoEx(file, &overlapped);
                    GetOverlappedResult(
                        file, &overlapped, &bytesTransferred, TRUE);
                    CloseHandle(ioEvent);
                    return ERROR_OPERATION_ABORTED;
                }
                if (waitResult != WAIT_OBJECT_0)
                {
                    DWORD waitError = GetLastError();
                    CancelIoEx(file, &overlapped);
                    GetOverlappedResult(
                        file, &overlapped, &bytesTransferred, TRUE);
                    CloseHandle(ioEvent);
                    // If WaitForMultipleObjects itself failed, use its error;
                    // otherwise (e.g. WAIT_ABANDONED), report a generic failure.
                    return waitResult == WAIT_FAILED && waitError != ERROR_SUCCESS
                        ? waitError
                        : ERROR_GEN_FAILURE;
                }
            }

            if (!GetOverlappedResult(
                    file, &overlapped, &bytesTransferred, FALSE))
            {
                DWORD error = GetLastError();
                CloseHandle(ioEvent);
                return error;
            }

            CloseHandle(ioEvent);
            return ERROR_SUCCESS;
        }
        void ReaderLoop()
        {
            ULONGLONG offset = 0;
            while (offset < pipelineLength_ &&
                   !cancelled_.load(std::memory_order_acquire))
            {
                BufferSlot& slot = slots_[producerIndex_];
                {
                    std::unique_lock<std::mutex> lock(queueMutex_);
                    freeCondition_.wait(lock, [&]
                    {
                        return cancelled_.load(std::memory_order_acquire) ||
                               !slot.ready;
                    });
                    if (cancelled_.load(std::memory_order_acquire))
                    {
                        break;
                    }
                }

                DWORD request = static_cast<DWORD>(
                    std::min<ULONGLONG>(
                        kBufferSize, pipelineLength_ - offset));
                DWORD bytesRead = 0;
                DWORD error = TransferAt(
                    false, source_, slot.data, request, offset, bytesRead);
                if (error != ERROR_SUCCESS)
                {
                    SetError(error);
                    break;
                }
                if (bytesRead == 0 || bytesRead > request)
                {
                    SetError(ERROR_READ_FAULT);
                    break;
                }

                {
                    std::lock_guard<std::mutex> lock(queueMutex_);
                    if (cancelled_.load(std::memory_order_acquire))
                    {
                        break;
                    }
                    slot.validBytes = bytesRead;
                    slot.offset = offset;
                    slot.ready = true;
                    offset += bytesRead;
                    producerIndex_ = (producerIndex_ + 1) % slots_.size();
                }
                readyCondition_.notify_one();
            }

            {
                std::lock_guard<std::mutex> lock(queueMutex_);
                readerComplete_ = true;
            }
            readyCondition_.notify_all();
        }

        void WriterLoop()
        {
            while (true)
            {
                BufferSlot* slot = nullptr;
                {
                    std::unique_lock<std::mutex> lock(queueMutex_);
                    readyCondition_.wait(lock, [&]
                    {
                        return cancelled_.load(std::memory_order_acquire) ||
                               slots_[consumerIndex_].ready ||
                               readerComplete_;
                    });

                    if (cancelled_.load(std::memory_order_acquire))
                    {
                        break;
                    }

                    BufferSlot& candidate = slots_[consumerIndex_];
                    if (!candidate.ready)
                    {
                        if (readerComplete_)
                        {
                            break;
                        }
                        continue;
                    }
                    slot = &candidate;
                }

                DWORD bytesWritten = 0;
                DWORD error = TransferAt(
                    true, destination_, slot->data, slot->validBytes,
                    slot->offset, bytesWritten);
                if (error == ERROR_SUCCESS &&
                    bytesWritten != slot->validBytes)
                {
                    error = ERROR_WRITE_FAULT;
                }

                ULONGLONG completedBytes = slot->validBytes;
                {
                    std::lock_guard<std::mutex> lock(queueMutex_);
                    slot->ready = false;
                    slot->validBytes = 0;
                    consumerIndex_ = (consumerIndex_ + 1) % slots_.size();
                }
                freeCondition_.notify_one();

                if (error != ERROR_SUCCESS)
                {
                    SetError(error);
                    break;
                }
                if (progressCallback_ &&
                    progressCallback_(completedBytes, progressContext_) != 0)
                {
                    Cancel();
                    break;
                }
            }
        }
        DWORD CopyBufferedTail(
            const wchar_t* sourcePath,
            const wchar_t* destinationPath,
            ULONGLONG offset,
            DWORD length)
        {
            if (length == 0)
            {
                return ERROR_SUCCESS;
            }

            // Close the Direct I/O handles before reopening for buffered tail.
            CloseFiles();
            DWORD error = OpenFiles(
                sourcePath, destinationPath, false, false);
            if (error != ERROR_SUCCESS)
            {
                return error;
            }

            DWORD bytesRead = 0;
            error = TransferAt(
                false, source_, slots_[0].data, length, offset, bytesRead);
            if (error == ERROR_SUCCESS && bytesRead != length)
            {
                error = ERROR_READ_FAULT;
            }

            DWORD bytesWritten = 0;
            if (error == ERROR_SUCCESS)
            {
                error = TransferAt(
                    true, destination_, slots_[0].data, bytesRead,
                    offset, bytesWritten);
                if (error == ERROR_SUCCESS && bytesWritten != bytesRead)
                {
                    error = ERROR_WRITE_FAULT;
                }
            }

            if (error == ERROR_SUCCESS && progressCallback_ &&
                progressCallback_(bytesWritten, progressContext_) != 0)
            {
                Cancel();
                error = ERROR_OPERATION_ABORTED;
            }
            if (error == ERROR_SUCCESS && !FlushFileBuffers(destination_))
            {
                error = GetLastError();
            }
            return error;
        }

        void SetError(DWORD error)
        {
            if (error == ERROR_SUCCESS)
            {
                error = ERROR_GEN_FAILURE;
            }
            DWORD expected = ERROR_SUCCESS;
            firstError_.compare_exchange_strong(
                expected, error, std::memory_order_acq_rel);
            Cancel();
        }

        HANDLE cancelEvent_ = nullptr;
        HANDLE source_ = INVALID_HANDLE_VALUE;
        HANDLE destination_ = INVALID_HANDLE_VALUE;
        std::mutex handleMutex_;

        std::atomic<bool> running_{false};
        std::atomic<bool> cancelled_{false};
        std::atomic<DWORD> firstError_{ERROR_SUCCESS};

        std::vector<BufferSlot> slots_;
        std::mutex queueMutex_;
        std::condition_variable freeCondition_;
        std::condition_variable readyCondition_;
        bool readerComplete_ = false;
        std::size_t producerIndex_ = 0;
        std::size_t consumerIndex_ = 0;

        ULONGLONG fileSize_ = 0;
        ULONGLONG pipelineLength_ = 0;
        DWORD alignment_ = 0;
        bool directIo_ = false;

        EzditNativeCopyProgressCallback progressCallback_ = nullptr;
        void* progressContext_ = nullptr;
    };
}
extern "C"
{
    DWORD __stdcall EZDIT_NativeCopyGetApiVersion()
    {
        return EZDIT_NATIVE_COPY_API_VERSION;
    }

    void* __stdcall EZDIT_NativeCopyCreate()
    {
        NativeCopyOperation* operation =
            new (std::nothrow) NativeCopyOperation();
        if (!operation || !operation->IsValid())
        {
            delete operation;
            return nullptr;
        }
        return operation;
    }

    void __stdcall EZDIT_NativeCopyCancel(void* operation)
    {
        if (operation)
        {
            static_cast<NativeCopyOperation*>(operation)->Cancel();
        }
    }

    void __stdcall EZDIT_NativeCopyDestroy(void* operation)
    {
        delete static_cast<NativeCopyOperation*>(operation);
    }

    DWORD __stdcall EZDIT_NativeCopyFileW(
        void* operation,
        const wchar_t* sourcePath,
        const wchar_t* destinationPath,
        DWORD flags,
        EzditNativeCopyProgressCallback progressCallback,
        void* progressContext)
    {
        if (!operation)
        {
            return ERROR_INVALID_HANDLE;
        }
        return static_cast<NativeCopyOperation*>(operation)->CopyFile(
            sourcePath, destinationPath, flags,
            progressCallback, progressContext);
    }
}