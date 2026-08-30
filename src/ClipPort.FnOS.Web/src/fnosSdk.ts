import { TrimApp } from '@trimjs/web-app';
import type { Language, Theme } from './types';

type PickerKind = 'source' | 'destination';
type AuthResult = { state?: string; status?: string; error?: string; data?: string[] };

export interface FnOsSdkAdapter {
  isStandaloneWeb: boolean;
  pickFolder(kind: PickerKind): Promise<string[]>;
  getPlatformConfig(): Promise<{ language: Language; theme: Theme }>;
  listen(onTheme: (theme: Theme) => void, onLanguage: (language: Language) => void): Promise<void>;
  parseCallback(url: string): AuthResult;
  setTitle(title: string): Promise<void>;
  openFileManager(path: string): Promise<void>;
  openApplicationSettings(): Promise<void>;
  openUrl(url: string): Promise<void>;
}

const sdk = new TrimApp();
const sidebarGroup = ['myFiles', 'otherShare', 'external', 'remote', 'favorites', 'team'] as const;

function normalizeLanguage(language?: string): Language {
  const normalized = language?.toLowerCase();
  return normalized?.startsWith('en')
    ? 'en-US'
    : normalized === 'lzh' || normalized?.startsWith('zh-classical')
      ? 'lzh'
      : 'zh-CN';
}

function callbackPath() {
  return new URL('callback.html', window.location.href.endsWith('/')
    ? window.location.href
    : `${window.location.href}/`).pathname;
}

export const fnosSdk: FnOsSdkAdapter = {
  isStandaloneWeb: sdk.isStandaloneWeb,

  async pickFolder(kind) {
    const creatable = kind === 'destination';
    const state = crypto.randomUUID();
    sessionStorage.setItem('clipport-auth-state', state);
    if (sdk.isStandaloneWeb) {
      const authParameters = {
        appName: 'clipport',
        sidebarGroup: [...sidebarGroup],
        creatable,
        redirectUri: callbackPath(),
        state,
      };
      // fnOS documents creatable for SharedFilePickerParams, while SDK 0.4.2
      // has not yet carried that field into AppAuthPickSharedFileParams.
      await sdk.openAppAuth('pickSharedFile', authParameters as never, {
        target: '_blank',
        features: 'width=750,height=630',
      });
      return [];
    }

    const result = await sdk.pickSharedFile({
      title: kind === 'source' ? '选择并授权源目录' : '选择并授权目标目录',
      okText: '确认授权',
      sidebarGroup: [...sidebarGroup],
      creatable,
    });
    if (result && result.code !== 0) {
      throw new Error(result.msg || 'fnOS folder authorization failed.');
    }
    return result?.data ?? [];
  },

  async getPlatformConfig() {
    const config = await sdk.getPlatformConfig();
    return {
      language: normalizeLanguage(config.language),
      theme: config.theme === 'dark' ? 'dark' : 'light',
    };
  },

  async listen(onTheme, onLanguage) {
    if (sdk.isWeb !== true || sdk.isStandaloneWeb === true) {
      return;
    }
    await sdk.$on('os/theme', theme => onTheme(theme === 'dark' ? 'dark' : 'light'));
    await sdk.$on('os/language', language => onLanguage(normalizeLanguage(language)));
  },

  parseCallback(url) {
    return sdk.parseAppAuthCallback(url) as AuthResult;
  },

  async setTitle(title) {
    await sdk.setTitle(title);
  },

  async openFileManager(path) {
    await sdk.openFileManager(path);
  },

  async openApplicationSettings() {
    await sdk.openAppSetting();
  },

  async openUrl(url) {
    await sdk.openURL(url, '_blank');
  },
};
