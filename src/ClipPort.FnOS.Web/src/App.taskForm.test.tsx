import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from './App';
import { translator } from './i18n';

const t = translator('zh-CN');

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

  async function choosePathsAndOpenDialog() {
    sdk.pickFolder.mockResolvedValueOnce(['/vol1/source']).mockResolvedValueOnce(['/vol1/destination']);
    fireEvent.click(screen.getByRole('button', { name: new RegExp(t('sourceStorageCard')) }));
    await waitFor(() => expect(sdk.pickFolder).toHaveBeenCalledWith('source'));
    fireEvent.click(screen.getByRole('button', { name: new RegExp(t('destinationCard')) }));
    await waitFor(() => expect(sdk.pickFolder).toHaveBeenCalledWith('destination'));
    fireEvent.click(screen.getByRole('button', { name: '开始拷卡' }));
    await screen.findByRole('dialog', { name: t('newTask') });
  }

  it('opens a reset dialog from New task and keeps draft paths from Start card copy', async () => {
    render(<App />); await screen.findByText('管理员 · admin');

    fireEvent.click(screen.getByRole('button', { name: t('newTask') }));
    let dialog = await screen.findByRole('dialog', { name: t('newTask') });
    expect(dialog).toHaveTextContent(t('dialogSourcePlaceholder'));
    expect(dialog).toHaveTextContent(t('dialogDestinationPlaceholder'));
    fireEvent.click(screen.getByRole('button', { name: t('cancel') }));

    sdk.pickFolder.mockResolvedValueOnce(['/vol1/source']).mockResolvedValueOnce(['/vol1/destination']);
    fireEvent.click(screen.getByRole('button', { name: new RegExp(t('sourceStorageCard')) }));
    await waitFor(() => expect(sdk.pickFolder).toHaveBeenCalledWith('source'));
    fireEvent.click(screen.getByRole('button', { name: new RegExp(t('destinationCard')) }));
    await waitFor(() => expect(sdk.pickFolder).toHaveBeenCalledWith('destination'));
    fireEvent.click(screen.getByRole('button', { name: '开始拷卡' }));

    dialog = await screen.findByRole('dialog', { name: t('newTask') });
    expect(dialog).toHaveTextContent('/vol1/source');
    expect(dialog).toHaveTextContent('/vol1/destination');
  });

  it('enforces verify-only and copy-only control states in dialog', async () => {
    render(<App />); await screen.findByText('管理员 · admin');
    await choosePathsAndOpenDialog();

    const copy = screen.getByLabelText(t('copyFilesAccessible'));
    const verify = screen.getByLabelText(t('verifyFilesAccessible'));

    // Uncheck copy -> verify-only mode (verify must stay on)
    fireEvent.click(copy);
    expect(verify).toBeChecked();

    // Uncheck verify -> copy-only mode (copy must stay on)
    fireEvent.click(copy);
    fireEvent.click(verify);
    expect(copy).toBeChecked();
    expect(verify).not.toBeChecked();
  });

  it('submits task creation from dialog', async () => {
    render(<App />); await screen.findByText('管理员 · admin');
    await choosePathsAndOpenDialog();

    // Toggle Windows-equivalent task options.
    fireEvent.click(screen.getByLabelText(t('opportunisticDuringCopy')));
    fireEvent.click(screen.getByLabelText(t('priorityAccessible')));

    // Submit
    fireEvent.click(screen.getByRole('button', { name: t('create') }));

    await waitFor(() => expect(api.createTask).toHaveBeenCalledWith(expect.objectContaining({
      mode: 'copyAndVerify', existingFilePolicy: 'ask', verificationAlgorithm: 'sha256',
      verificationExecutionMode: 'opportunisticDuringCopy', isPriority: true,
    })));
  });
});
