import { beforeEach, describe, expect, it, vi } from 'vitest';

const sdk = vi.hoisted(() => ({
  isStandaloneWeb: false,
  isWeb: true,
  pickSharedFile: vi.fn(),
  openAppAuth: vi.fn(),
  getPlatformConfig: vi.fn(async () => ({ language: 'zh-CN', theme: 'light' })),
  $on: vi.fn(),
  parseAppAuthCallback: vi.fn(),
  setTitle: vi.fn(),
}));

vi.mock('@trimjs/web-app', () => ({ TrimApp: class { constructor() { return sdk; } } }));

describe('fnOS folder authorization adapter', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    sdk.isStandaloneWeb = false;
  });

  it('calls the native picker and disables source folder creation', async () => {
    sdk.pickSharedFile.mockResolvedValue({ code: 0, msg: '', data: ['/vol1/source'] });
    const { fnosSdk } = await import('./fnosSdk');
    await expect(fnosSdk.pickFolder('source')).resolves.toEqual(['/vol1/source']);
    expect(sdk.pickSharedFile).toHaveBeenCalledWith(expect.objectContaining({ creatable: false }));
  });

  it('allows destination folder creation', async () => {
    sdk.pickSharedFile.mockResolvedValue({ code: 0, msg: '', data: ['/vol1/target'] });
    const { fnosSdk } = await import('./fnosSdk');
    await fnosSdk.pickFolder('destination');
    expect(sdk.pickSharedFile).toHaveBeenCalledWith(expect.objectContaining({ creatable: true }));
  });

  it('treats closing the native picker as cancellation', async () => {
    sdk.pickSharedFile.mockResolvedValue(undefined);
    const { fnosSdk } = await import('./fnosSdk');
    await expect(fnosSdk.pickFolder('source')).resolves.toEqual([]);
  });

  it('surfaces an SDK business rejection instead of treating it as cancellation', async () => {
    sdk.pickSharedFile.mockResolvedValue({ code: 200006, msg: 'authorization denied', data: [] });
    const { fnosSdk } = await import('./fnosSdk');
    await expect(fnosSdk.pickFolder('source')).rejects.toThrow('authorization denied');
  });

  it('uses openAppAuth in a standalone browser', async () => {
    sdk.isStandaloneWeb = true;
    vi.resetModules();
    const { fnosSdk } = await import('./fnosSdk');
    await fnosSdk.pickFolder('source');
    expect(sdk.openAppAuth).toHaveBeenCalledWith('pickSharedFile', expect.objectContaining({
      appName: 'clipport', redirectUri: expect.stringContaining('callback.html'),
    }), expect.objectContaining({ target: '_blank' }));
    expect(sdk.pickSharedFile).not.toHaveBeenCalled();
  });
});
