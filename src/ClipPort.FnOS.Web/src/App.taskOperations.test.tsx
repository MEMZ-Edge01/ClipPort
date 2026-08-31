import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from './App';
import type { ClipPortTask } from './types';

const api = vi.hoisted(() => ({
  loadSession: vi.fn(), loadFolders: vi.fn(), loadTasks: vi.fn(), loadSettings: vi.fn(),
  taskAction: vi.fn(), deleteTasks: vi.fn(), exportReports: vi.fn(), deleteTask: vi.fn(),
  submitDuplicates: vi.fn(), submitFailures: vi.fn(),
}));
const sdk = vi.hoisted(() => ({
  isStandaloneWeb: false, pickFolder: vi.fn(), getPlatformConfig: vi.fn(), listen: vi.fn(),
  parseCallback: vi.fn(), setTitle: vi.fn(), openFileManager: vi.fn(), openApplicationSettings: vi.fn(), openUrl: vi.fn(),
}));
vi.mock('./api', async importOriginal => ({ ...await importOriginal<typeof import('./api')>(), ...api }));
vi.mock('./fnosSdk', () => ({ fnosSdk: sdk }));

class SilentWebSocket { onmessage: ((event: MessageEvent) => void) | null = null; onclose: (() => void) | null = null; close() {} }

const task = (id: string, status: ClipPortTask['status'], patch: Partial<ClipPortTask> = {}): ClipPortTask => ({
  id, status, displayName: id, createdAt: '2026-08-30T00:00:00Z',
  request: { mode: 'copyAndVerify', sourcePath: '/vol1/source', destinationPath: '/vol1/target', existingFilePolicy: 'ask', verificationAlgorithm: 'sha256', verificationExecutionMode: 'afterCopy', isPriority: false },
  duplicateFiles: [], failedFiles: [], errors: [], warnings: [], reportFileName: `${id}.txt`,
  copyByteSpeedSamples: [10, 20], copyItemSpeedSamples: [1, 2], copyThroughputProgressSamples: [0, 1],
  verifyByteSpeedSamples: [8, 12], verifyItemSpeedSamples: [1, 1], verifyThroughputProgressSamples: [0, 1],
  ...patch,
});

describe('task operations, selections and decisions', () => {
  beforeEach(() => {
    vi.clearAllMocks(); vi.stubGlobal('WebSocket', SilentWebSocket);
    api.loadSession.mockResolvedValue({ isAdmin: true, userId: 1000, username: 'admin', csrfToken: 'csrf', language: 'zh-CN', systemVersion: '1.2.0401', isCompatible: true });
    api.loadFolders.mockResolvedValue([]);
    api.loadSettings.mockResolvedValue({ version: 1, theme: 'system', accent: 'system', language: 'simplifiedChinese', reportExportDirectory: '/vol1/reports', notifyOnTaskCompleted: true, notifyOnTaskFailed: true, channels: [] });
    api.taskAction.mockResolvedValue(undefined); api.deleteTasks.mockResolvedValue(undefined); api.deleteTask.mockResolvedValue(undefined);
    api.exportReports.mockResolvedValue({ exportedCount: 1, files: ['/vol1/reports/done.txt'] });
    api.submitDuplicates.mockResolvedValue(undefined); api.submitFailures.mockResolvedValue(undefined);
    sdk.getPlatformConfig.mockResolvedValue({ language: 'zh-CN', theme: 'light' }); sdk.listen.mockResolvedValue(undefined); sdk.setTitle.mockResolvedValue(undefined);
  });

  it('selects tasks in sidebar and performs actions in content area', async () => {
    api.loadTasks.mockResolvedValue([task('running-job', 'running'), task('paused-job', 'paused'), task('done-job', 'completed')]);
    render(<App />); await screen.findByText('管理员 · admin');

    // Tasks should appear in sidebar
    expect(screen.getByText('running-job')).toBeInTheDocument();
    expect(screen.getByText('paused-job')).toBeInTheDocument();
    expect(screen.getByText('done-job')).toBeInTheDocument();

    // Click on running job card in sidebar
    fireEvent.click(screen.getByText('running-job'));
    // Content area should show pause button (use role to be specific)
    const pauseBtn = await screen.findByRole('button', { name: /暂停/ });
    fireEvent.click(pauseBtn);
    await waitFor(() => expect(api.taskAction).toHaveBeenCalledWith('running-job', 'pause'));
  });

  it('submits duplicate choices via dialog', async () => {
    api.loadTasks.mockResolvedValue([task('dup-job', 'awaitingDuplicateDecision', {
      duplicateFiles: [{ relativePath: 'a.txt' }, { relativePath: 'b.txt' }], reportFileName: null,
    })]);
    render(<App />); await screen.findByText('管理员 · admin');
    const dialog = await screen.findByRole('dialog', { name: '需要处理重复文件' });

    // Batch apply overwrite to all - use the button in the batch actions area
    const batchButtons = within(dialog).getAllByText('覆盖');
    fireEvent.click(batchButtons[0]);
    // Change second item to skip via combobox
    fireEvent.change(within(dialog).getAllByRole('combobox')[1], { target: { value: 'skip' } });
    fireEvent.click(within(dialog).getByText('提交决定'));
    await waitFor(() => expect(api.submitDuplicates).toHaveBeenCalledWith('dup-job', [
      { relativePath: 'a.txt', decision: 'overwrite' }, { relativePath: 'b.txt', decision: 'skip' },
    ]));
  });

  it('submits selected failed items for retry', async () => {
    api.loadTasks.mockResolvedValue([task('fail-job', 'awaitingFailureDecision', {
      failedFiles: [{ relativePath: 'broken.txt', error: 'failed', isVerificationMismatch: false }], reportFileName: null,
    })]);
    render(<App />); await screen.findByText('管理员 · admin');
    const dialog = await screen.findByRole('dialog', { name: '需要处理失败项' });
    expect(within(dialog).getByText('需要处理失败项')).toBeInTheDocument();
    await waitFor(() => expect(within(dialog).getByRole('checkbox')).toBeChecked());
    fireEvent.click(within(dialog).getByText('重试所选'));
    await waitFor(() => expect(api.submitFailures).toHaveBeenCalledWith('fail-job', 'retry', ['broken.txt']));
  });
});
