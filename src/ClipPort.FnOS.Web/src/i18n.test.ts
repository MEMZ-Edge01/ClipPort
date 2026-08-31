import { describe, expect, it } from 'vitest';
import { platformOnlyTranslationKeys, translator } from './i18n';
import type { TranslationKey } from './i18n';
import { sharedWindowsResourceKeys, windowsResources } from './generatedTranslations';
import type { Language } from './types';

describe('generated Windows resource localization', () => {
  it('supports Simplified Chinese, English and Classical Chinese', () => {
    expect(translator('zh-CN')('pause')).toBe('暂停');
    expect(translator('en-US')('pause')).toBe('Pause');
    expect(translator('lzh')('pause')).not.toBe('Pause');
    expect(translator('lzh')('newTask')).toBe(windowsResources.lzh['NewJobButtonText.Text']);
  });

  it('uses Windows resource wording for shared task controls', () => {
    const zh = translator('zh-CN');
    expect(zh('selectAll')).toBe(windowsResources['zh-CN']['Button.SelectAll']);
    expect(zh('opportunisticDuringCopy')).toBe(
      windowsResources['zh-CN']['OpportunisticVerificationToggle.Header'],
    );
  });

  it('keeps every mapped shared string synchronized in all languages', () => {
    for (const language of ['zh-CN', 'en-US', 'lzh'] as const satisfies readonly Language[]) {
      const t = translator(language);
      for (const [translationKey, resourceKey] of Object.entries(sharedWindowsResourceKeys)) {
        expect(t(translationKey as TranslationKey), `${language}:${translationKey}`).toBe(
          (windowsResources[language] as Record<string, string>)[resourceKey],
        );
      }
      expect(t('authorizationUnavailable')).not.toHaveLength(0);
      expect(t('retryAuthorization')).not.toHaveLength(0);
      for (const key of platformOnlyTranslationKeys) expect(t(key), `${language}:${key}`).not.toHaveLength(0);
    }
  });
});
