import { fireEvent, render, screen, waitFor } from '@testing-library/react';
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
      isAdmin: true, userId: 1000, username: 'admin', csrfToken: 'csrf',
      language: 'zh-CN', systemVersion: '1.2.0401', isCompatible: true,
    });
    api.loadFolders.mockResolvedValueOnce([]);
    api.loadTasks.mockResolvedValue([]);
    api.loadSettings.mockResolvedValue({
      version: 1, theme: 'system', accent: 'system', language: 'simplifiedChinese',
      reportExportDirectory: null, notifyOnTaskCompleted: true, notifyOnTaskFailed: true, channels: [],
    });
    sdk.getPlatformConfig.mockResolvedValue({ language: 'zh-CN', theme: 'light' });
    sdk.listen.mockResolvedValue(undefined);
    sdk.setTitle.mockResolvedValue(undefined);
  });

  it('keeps a selected source and retries authorization propagation without showing an operation failure', async () => {
    sdk.pickFolder.mockResolvedValue(['/vol1/share/Camera Uploads']);
    api.loadFolders.mockRejectedValueOnce(new Error('gateway rejected refresh'));
    api.loadFolders.mockResolvedValueOnce([{
      path: '/vol1/share/Camera Uploads', semanticPath: '共享文件/Camera Uploads',
      readable: true, writable: true, permissionState: 'confirmed', semanticPathState: 'confirmed',
    }]);
    render(<App />);
    await screen.findByText('管理员 · admin');
    await waitFor(() => expect(api.loadFolders).toHaveBeenCalledTimes(1));

    fireEvent.click(screen.getByRole('button', { name: /源目录 \/ 存储卡/ }));

    await waitFor(() => expect(sdk.pickFolder).toHaveBeenCalledWith('source'));

    expect(await screen.findByText('/vol1/share/Camera Uploads')).toBeInTheDocument();

    await waitFor(() => expect(api.loadFolders).toHaveBeenCalledTimes(3));
    expect(screen.queryByText(/授权状态仍在同步/)).not.toBeInTheDocument();
    expect(screen.queryByText(/操作失败.*gateway rejected refresh/)).not.toBeInTheDocument();
  });

  it('keeps the path and offers a retry when authorization propagation is still pending', async () => {
    sdk.pickFolder.mockResolvedValue(['/vol1/share/Camera Uploads']);
    api.loadFolders.mockRejectedValue(new Error('gateway rejected refresh'));
    api.loadFolders.mockResolvedValueOnce([]);
    render(<App />);
    await screen.findByText('管理员 · admin');
    await waitFor(() => expect(api.loadFolders).toHaveBeenCalledTimes(1));
    fireEvent.click(screen.getByRole('button', { name: /源目录 \/ 存储卡/ }));

    expect(await screen.findByText(/目录已选择，授权状态仍在同步/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '重新同步' })).toBeInTheDocument();
    expect(screen.getByText('/vol1/share/Camera Uploads')).toBeInTheDocument();
  });
});
