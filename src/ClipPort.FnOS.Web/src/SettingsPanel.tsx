import { useEffect, useState } from 'react';
import { testNotification } from './api';
import { translator } from './i18n';
import type { AppSettings, Language, NotificationChannel } from './types';

export function SettingsPanel({
  value,
  language,
  onSave,
  onChooseReportDirectory,
}: {
  value: AppSettings;
  language: Language;
  onSave(value: AppSettings): Promise<void>;
  onChooseReportDirectory(): Promise<string | undefined>;
}) {
  const [draft, setDraft] = useState(value);
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);
  const t = translator(language);
  useEffect(() => setDraft(value), [value]);

  const updateChannel = (id: string, patch: Partial<NotificationChannel>) =>
    setDraft(current => ({
      ...current,
      channels: current.channels.map(channel => channel.id === id ? { ...channel, ...patch } : channel),
    }));

  const addChannel = () => setDraft(current => ({
    ...current,
    channels: [...current.channels, {
      id: crypto.randomUUID(), displayName: '', kind: 'feishu', isEnabled: true,
      hasEndpoint: false, smtpHost: '', smtpPort: 465, smtpUsername: '',
      hasSmtpPassword: false, smtpFrom: '', smtpRecipients: '',
    }],
  }));

  const save = async () => {
    setBusy(true); setMessage('');
    try {
      await onSave(draft);
      setMessage(t('settingsSaved'));
    } catch (error) {
      setMessage(error instanceof Error ? error.message : t('operationFailed'));
    } finally { setBusy(false); }
  };

  return <section className="settings-layout">
    <div className="panel settings-card">
      <div className="section-heading"><div><span className="eyebrow">FNOS</span><h2>{t('appearance')}</h2></div></div>
      <div className="form-grid">
        <label>{t('theme')}<select value={draft.theme} onChange={event => setDraft({ ...draft, theme: event.target.value as AppSettings['theme'] })}>
          <option value="system">{t('system')}</option><option value="light">{t('light')}</option><option value="dark">{t('dark')}</option>
        </select></label>
        <label>{t('accent')}<select value={draft.accent} onChange={event => setDraft({ ...draft, accent: event.target.value as AppSettings['accent'] })}>
          {(['system', 'seafoam', 'brightRose', 'gold', 'mint', 'purpleShadow'] as const).map(accent => <option key={accent} value={accent}>{t(accent)}</option>)}
        </select></label>
        <label>{t('language')}<select value={draft.language} onChange={event => setDraft({ ...draft, language: event.target.value as AppSettings['language'] })}>
          <option value="simplifiedChinese">{t('simplifiedChinese')}</option><option value="english">English</option><option value="classicalChinese">{t('classicalChinese')}</option>
        </select></label>
      </div>
    </div>

    <div className="panel settings-card">
      <div className="section-heading"><div><span className="eyebrow">REPORTS</span><h2>{t('general')}</h2></div></div>
      <label>{t('reportExportDirectory')}<div className="inline-field"><input readOnly value={draft.reportExportDirectory ?? ''} /><button type="button" onClick={() => void onChooseReportDirectory().then(path => path && setDraft(current => ({ ...current, reportExportDirectory: path })))}>{t('chooseReportDirectory')}</button></div></label>
    </div>

    <div className="panel settings-card notification-settings">
      <div className="section-heading"><div><span className="eyebrow">WEBHOOK · SMTP</span><h2>{t('notification')}</h2></div><button type="button" onClick={addChannel}>{t('addChannel')}</button></div>
      <div className="toggle-row"><label><input type="checkbox" checked={draft.notifyOnTaskCompleted} onChange={event => setDraft({ ...draft, notifyOnTaskCompleted: event.target.checked })} />{t('notifyCompleted')}</label>
        <label><input type="checkbox" checked={draft.notifyOnTaskFailed} onChange={event => setDraft({ ...draft, notifyOnTaskFailed: event.target.checked })} />{t('notifyFailed')}</label></div>
      <div className="channel-list">{draft.channels.map(channel => <article className="channel-card" key={channel.id}>
        <div className="channel-heading"><label className="checkbox-label"><input type="checkbox" checked={channel.isEnabled} onChange={event => updateChannel(channel.id, { isEnabled: event.target.checked })} />{t('enabled')}</label>
          <button className="danger-text" onClick={() => setDraft(current => ({ ...current, channels: current.channels.filter(item => item.id !== channel.id) }))}>{t('deleteChannel')}</button></div>
        <div className="form-grid">
          <label>{t('channelName')}<input value={channel.displayName} onChange={event => updateChannel(channel.id, { displayName: event.target.value })} /></label>
          <label>{t('channelKind')}<select value={channel.kind} onChange={event => updateChannel(channel.id, { kind: event.target.value as NotificationChannel['kind'] })}>
            {(['weCom', 'dingTalk', 'feishu', 'bark', 'smtp'] as const).map(kind => <option key={kind} value={kind}>{t(kind)}</option>)}
          </select></label>
          {channel.kind !== 'smtp' ? <label className="wide-field">{t('endpoint')}<input type="password" value={channel.endpoint ?? ''} placeholder={channel.hasEndpoint ? t('savedSecret') : ''} onChange={event => updateChannel(channel.id, { endpoint: event.target.value, clearEndpoint: false })} /></label> : <>
            <label>{t('smtpHost')}<input value={channel.smtpHost} onChange={event => updateChannel(channel.id, { smtpHost: event.target.value })} /></label>
            <label>{t('smtpPort')}<input type="number" value={channel.smtpPort} onChange={event => updateChannel(channel.id, { smtpPort: Number(event.target.value) })} /></label>
            <label>{t('smtpUsername')}<input value={channel.smtpUsername} onChange={event => updateChannel(channel.id, { smtpUsername: event.target.value })} /></label>
            <label>{t('smtpPassword')}<input type="password" value={channel.smtpPassword ?? ''} placeholder={channel.hasSmtpPassword ? t('savedSecret') : ''} onChange={event => updateChannel(channel.id, { smtpPassword: event.target.value, clearSmtpPassword: false })} /></label>
            <label>{t('smtpFrom')}<input value={channel.smtpFrom} onChange={event => updateChannel(channel.id, { smtpFrom: event.target.value })} /></label>
            <label>{t('smtpRecipients')}<input value={channel.smtpRecipients} onChange={event => updateChannel(channel.id, { smtpRecipients: event.target.value })} /></label>
          </>}
        </div>
        <button type="button" onClick={() => void testNotification(channel).then(result => setMessage(result.detail)).catch(error => setMessage(error instanceof Error ? error.message : t('operationFailed')))}>{t('testSend')}</button>
      </article>)}</div>
      <div className="settings-actions"><span>{message}</span><button className="primary" disabled={busy} onClick={() => void save()}>{t('saveSettings')}</button></div>
    </div>
  </section>;
}
