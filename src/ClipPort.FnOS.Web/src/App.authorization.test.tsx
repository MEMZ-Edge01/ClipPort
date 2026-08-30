import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from './App';

const api = vi.hoisted(() => ({
  loadSession: vi.fn(),
  loadFolders: vi.fn(),
  loadTasks: vi.fn(),
  loadSettings: vi.fn(),
}));

const sdk = vi.hoisted(() => ({
  isStandaloneWeb: false,
  pickFolder: vi.fn(),
  getPlatformConfig: vi.fn(),
  listen: vi.fn(),
  parseCallback: vi.fn(),
  setTitle: vi.fn(),
}));

vi.mock('./api', async importOriginal => ({
  ...await importOriginal<typeof import('./api')>(),
  loadSession: api.loadSession,
  loadFolders: api.loadFolders,
  loadTasks: api.loadTasks,
  loadSettings: api.loadSettings,
}));
vi.mock('./fnosSdk', () => ({ fnosSdk: sdk }));

class SilentWebSocket {
  static readonly OPEN = 1;
  onmessage: ((event: MessageEvent) => void) | null = null;
  onclose: (() => void) | null = null;
  close() {}
}

describe('folder selection flow', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('WebSocket', SilentWebSocket);
    api.loadSession.mockResolvedValue({
      isAdmin: true,
      userId: 1000,
      username: 'admin',
      csrfToken: 'csrf',
      language: 'zh-CN',
      systemVersion: '1.2.0401',
      isCompatible: true,
    });
    api.loadFolders.mockResolvedValueOnce([]);
    api.loadTasks.mockResolvedValue([]);
    api.loadSettings.mockResolvedValue({
      version: 1,
      theme: 'system',
      accent: 'system',
      language: 'simplifiedChinese',
      reportExportDirectory: null,
      notifyOnTaskCompleted: true,
      notifyOnTaskFailed: true,
      channels: [],
    });
    sdk.getPlatformConfig.mockResolvedValue({ language: 'zh-CN', theme: 'light' });
    sdk.listen.mockResolvedValue(undefined);
    sdk.setTitle.mockResolvedValue(undefined);
  });

  it('keeps a selected source and generates its subfolder when authorization refresh fails', async () => {
    sdk.pickFolder.mockResolvedValue(['/vol1/share/Camera Uploads']);
    api.loadFolders.mockRejectedValueOnce(new Error('gateway rejected refresh'));
    render(<App />);
    await screen.findByText('管理员 · admin');

    fireEvent.click(screen.getByRole('button', { name: '选择并授权源目录' }));

    const source = screen.getByRole('group', { name: '源目录' });
    await waitFor(() => expect(within(source).getByRole('combobox')).toHaveValue('/vol1/share/Camera Uploads'));
    expect(screen.getByLabelText('目标子目录（可选）')).toHaveValue('Camera Uploads');
    expect(await screen.findByText('目录已选择，但授权列表同步失败。')).toBeInTheDocument();
    expect(screen.queryByText(/操作失败.*gateway rejected refresh/)).not.toBeInTheDocument();
  });
});
