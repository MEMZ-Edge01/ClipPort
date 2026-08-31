import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { SettingsPanel } from './SettingsPanel';
import { translator } from './i18n';
import type { AppSettings } from './types';

vi.mock('./api', () => ({ testNotification: vi.fn(async () => ({ success: true, detail: 'ok' })) }));

describe('fnOS settings and notification form', () => {
  it('preserves saved secrets while editing all five channel kinds', async () => {
    const t = translator('zh-CN');
    const onSave = vi.fn<(value: AppSettings) => Promise<void>>(async () => undefined);
    render(<SettingsPanel language="zh-CN" onSave={onSave} onChooseReportDirectory={async () => '/vol1/reports'} value={{
      version: 1, theme: 'system', accent: 'system', language: 'simplifiedChinese',
      reportExportDirectory: null, notifyOnTaskCompleted: true, notifyOnTaskFailed: true,
      channels: [{ id: 'saved', displayName: '飞书', kind: 'feishu', isEnabled: true,
        hasEndpoint: true, smtpHost: '', smtpPort: 465, smtpUsername: '', hasSmtpPassword: false,
        smtpFrom: '', smtpRecipients: '' }],
    }} />);

    expect(screen.getByLabelText('Webhook / Bark 地址')).toHaveAttribute('placeholder', '已安全保存；留空则保留');
    const kind = screen.getByLabelText(t('channelKind'));
    expect([...kind.querySelectorAll('option')].map(option => option.value)).toEqual(['weCom', 'dingTalk', 'feishu', 'bark', 'smtp']);
    fireEvent.change(kind, { target: { value: 'smtp' } });
    expect(screen.getByLabelText('密码')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '选择报告导出目录' }));
    await waitFor(() => expect(screen.getByDisplayValue('/vol1/reports')).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: '保存设置' }));

    await waitFor(() => expect(onSave).toHaveBeenCalled());
    const saved = onSave.mock.calls[0][0];
    expect(saved.reportExportDirectory).toBe('/vol1/reports');
    expect(saved.channels[0]).toEqual(expect.objectContaining({ id: 'saved', kind: 'smtp' }));
    expect(saved.channels[0]).not.toHaveProperty('endpoint');
  });
});
