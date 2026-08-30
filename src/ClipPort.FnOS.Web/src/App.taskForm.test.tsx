import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from './App';

const api = vi.hoisted(() => ({
  loadSession: vi.fn(), loadFolders: vi.fn(), loadTasks: vi.fn(), loadSettings: vi.fn(), createTask: vi.fn(),
}));
const sdk = vi.hoisted(() => ({
  isStandaloneWeb: false, pickFolder: vi.fn(), getPlatformConfig: vi.fn(), listen: vi.fn(),
  parseCallback: vi.fn(), setTitle: vi.fn(), openFileManager: vi.fn(), openApplicationSettings: vi.fn(), openUrl: vi.fn(),
}));
vi.mock('./api', async importOriginal => ({ ...await importOriginal<typeof import('./api')>(), ...api }));
vi.mock('./fnosSdk', () => ({ fnosSdk: sdk }));

class SilentWebSocket { onmessage: ((event: MessageEvent) => void) | null = null; onclose: (() => void) | null = null; close() {} }

describe('Windows-equivalent task mode logic', () => {
  beforeEach(() => {
    vi.clearAllMocks(); vi.stubGlobal('WebSocket', SilentWebSocket);
    api.loadSession.mockResolvedValue({ isAdmin: true, userId: 1000, username: 'admin', csrfToken: 'csrf', language: 'zh-CN', systemVersion: '1.2.0401', isCompatible: true });
    api.loadFolders.mockResolvedValue([
      { path: '/vol1/source', semanticPath: '源', readable: true, writable: true },
      { path: '/vol1/destination', semanticPath: '目标', readable: true, writable: true },
    ]);
    api.loadTasks.mockResolvedValue([]);
    api.loadSettings.mockResolvedValue({ version: 1, theme: 'system', accent: 'system', language: 'simplifiedChinese', reportExportDirectory: null, notifyOnTaskCompleted: true, notifyOnTaskFailed: true, channels: [] });
    api.createTask.mockResolvedValue({ id: 'created', displayName: 'source', request: { mode: 'copyAndVerify', sourcePath: '/vol1/source', destinationPath: '/vol1/destination', existingFilePolicy: 'ask', verificationAlgorithm: 'sha256', verificationExecutionMode: 'afterCopy', isPriority: true }, status: 'queued', createdAt: new Date().toISOString(), duplicateFiles: [], failedFiles: [], errors: [], warnings: [], copyByteSpeedSamples: [], copyItemSpeedSamples: [], copyThroughputProgressSamples: [], verifyByteSpeedSamples: [], verifyItemSpeedSamples: [], verifyThroughputProgressSamples: [] });
    sdk.getPlatformConfig.mockResolvedValue({ language: 'zh-CN', theme: 'light' }); sdk.listen.mockResolvedValue(undefined); sdk.setTitle.mockResolvedValue(undefined);
  });

  it('enforces verify-only and copy-only control states', async () => {
    render(<App />); await screen.findByText('管理员 · admin');
    const copy = screen.getByLabelText('复制文件'); const verify = screen.getByLabelText('校验文件');
    const subfolder = screen.getByLabelText('目标子目录（可选）'); const duplicate = screen.getByLabelText('重复文件');
    const algorithm = screen.getByLabelText('校验算法'); const timing = screen.getByLabelText('校验时机');

    fireEvent.click(copy);
    expect(verify).toBeChecked(); expect(subfolder).toBeDisabled(); expect(duplicate).toBeDisabled();
    expect(algorithm).toBeEnabled(); expect(timing).toBeDisabled();

    fireEvent.click(copy); fireEvent.click(verify);
    expect(copy).toBeChecked(); expect(verify).not.toBeChecked();
    expect(subfolder).toBeEnabled(); expect(duplicate).toBeEnabled();
    expect(algorithm).toBeDisabled(); expect(timing).toBeDisabled();
  });

  it('submits SHA-256, ask, after-copy and priority defaults explicitly', async () => {
    render(<App />); await screen.findByText('管理员 · admin');
    const groups = screen.getAllByRole('group');
    fireEvent.change(groups[0].querySelector('select')!, { target: { value: '/vol1/source' } });
    fireEvent.change(groups[1].querySelector('select')!, { target: { value: '/vol1/destination' } });
    fireEvent.click(screen.getByLabelText('优先'));
    fireEvent.click(screen.getByRole('button', { name: '创建任务' }));

    await waitFor(() => expect(api.createTask).toHaveBeenCalledWith(expect.objectContaining({
      mode: 'copyAndVerify', existingFilePolicy: 'ask', verificationAlgorithm: 'sha256',
      verificationExecutionMode: 'afterCopy', isPriority: true,
    })));
  });
});
