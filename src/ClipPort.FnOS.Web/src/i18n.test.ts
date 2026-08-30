import { describe, expect, it } from 'vitest';
import { translator } from './i18n';

describe('generated Windows resource localization', () => {
  it('supports Simplified Chinese, English and Classical Chinese', () => {
    expect(translator('zh-CN')('pause')).toBe('暂停');
    expect(translator('en-US')('pause')).toBe('Pause');
    expect(translator('lzh')('pause')).not.toBe('Pause');
    expect(translator('lzh')('newTask')).toBe('立新事');
  });
});
