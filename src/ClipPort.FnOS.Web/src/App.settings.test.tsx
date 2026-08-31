import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from './App';

const settings = {
  version: 1, theme: 'system' as const, accent: 'system' as const, language: 'simplifiedChinese' as const,
  reportExportDirectory: null, notifyOnTaskCompleted: true, notifyOnTaskFailed: true, channels: [],
};
const api = vi.hoisted(() => ({
  loadSession: vi.fn(), loadFolders: vi.fn(), loadTasks: vi.fn(), loadSettings: vi.fn(), saveSettings: vi.fn(),
}));
const sdk = vi.hoisted(() => ({
  isStandaloneWeb: false, pickFolder: vi.fn(), getPlatformConfig: vi.fn(), listen: vi.fn(), parseCallback: vi.fn(),
  setTitle: vi.fn(), openFileManager: vi.fn(), openApplicationSettings: vi.fn(), openUrl: vi.fn(),
}));
vi.mock('./api', async importOriginal => ({ ...await importOriginal<typeof import('./api')>(), ...api }));
vi.mock('./fnosSdk', () => ({ fnosSdk: sdk }));

class SilentWebSocket { onmessage: ((event: MessageEvent) => void) | null = null; onclose: (() => void) | null = null; close() {} }

describe('unified Windows-style settings navigation', () => {
  beforeEach(() => {
    vi.clearAllMocks(); vi.stubGlobal('WebSocket', SilentWebSocket);
    api.loadSession.mockResolvedValue({ isAdmin: true, userId: 1000, username: 'admin', csrfToken: 'csrf', language: 'zh-CN', systemVersion: '1.2.0401', isCompatible: true });
    api.loadFolders.mockResolvedValue([{ path: '/vol1/share', semanticPath: '共享文件', readable: true, writable: true, permissionState: 'confirmed', semanticPathState: 'confirmed' }]);
    api.loadTasks.mockResolvedValue([]); api.loadSettings.mockResolvedValue(settings); api.saveSettings.mockImplementation(async value => value);
    sdk.getPlatformConfig.mockResolvedValue({ language: 'zh-CN', theme: 'light' }); sdk.listen.mockResolvedValue(undefined); sdk.setTitle.mockResolvedValue(undefined);
  });

  it('keeps all five sections in one sidebar and returns to the draft task', async () => {
    render(<App />); await screen.findByText('管理员 · admin');
    fireEvent.click(screen.getByRole('button', { name: '设置' }));
    for (const label of ['外观', '常规', '授权目录', '通知', '关于']) {
      expect(screen.getByRole('button', { name: label })).toBeInTheDocument();
    }
    fireEvent.click(screen.getByRole('button', { name: '授权目录' }));
    expect(await screen.findByText('共享文件')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '关于' }));
    expect(await screen.findByText('应用信息')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '返回任务' }));
    expect(await screen.findByText('准备新任务')).toBeInTheDocument();
  });

  it('previews and saves theme and accent changes immediately', async () => {
    render(<App />); await screen.findByText('管理员 · admin');
    fireEvent.click(screen.getByRole('button', { name: '设置' }));
    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'dark' } });
    await waitFor(() => expect(document.documentElement.dataset.theme).toBe('dark'));
    fireEvent.click(screen.getByTitle('亮玫红'));
    await waitFor(() => expect(document.documentElement.dataset.accent).toBe('brightRose'));
    expect(api.saveSettings).toHaveBeenCalledWith(expect.objectContaining({ theme: 'dark' }));
    expect(api.saveSettings).toHaveBeenCalledWith(expect.objectContaining({ accent: 'brightRose' }));
  });

  it('loads tasks even when the host platform configuration is unavailable', async () => {
    sdk.getPlatformConfig.mockRejectedValue(new Error('host config unavailable'));
    render(<App />);
    expect(await screen.findByText('准备新任务')).toBeInTheDocument();
    expect(screen.queryByText(/host config unavailable/)).not.toBeInTheDocument();
  });
});
