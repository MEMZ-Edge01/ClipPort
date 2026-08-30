import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ApiError, checkUpdate, createTask, deleteTask, deleteTasks, exportReports, loadFolders,
  loadSession, loadSettings, loadTasks, reportUrl, revokeFolder, saveSettings,
  submitDuplicates, submitFailures, taskAction, validateFolder, websocketUrl,
} from './api';
import { fnosSdk } from './fnosSdk';
import { translator } from './i18n';
import { SettingsPanel } from './SettingsPanel';
import { Waveform } from './Waveform';
import type {
  AppSettings, AuthorizedFolder, ClipPortTask, ExistingPolicy, HashAlgorithm,
  Language, Session, Theme, UpdateMetadata,
} from './types';

const terminalStatuses = new Set<ClipPortTask['status']>([
  'completed', 'completedWithErrors', 'verificationFailed', 'failed', 'cancelled', 'interrupted',
]);
const activeStatuses = new Set<ClipPortTask['status']>([
  'queued', 'running', 'paused', 'awaitingDuplicateDecision', 'awaitingFailureDecision',
]);
type View = 'new' | 'running' | 'history' | 'authorization' | 'settings' | 'about';

interface TaskForm {
  enableCopy: boolean;
  verifyFiles: boolean;
  sourcePath: string;
  destinationPath: string;
  destinationSubfolder: string;
  existingFilePolicy: ExistingPolicy;
  verificationAlgorithm: HashAlgorithm;
  verificationExecutionMode: 'afterCopy' | 'opportunisticDuringCopy';
  isPriority: boolean;
}

const initialForm: TaskForm = {
  enableCopy: true, verifyFiles: true, sourcePath: '', destinationPath: '',
  destinationSubfolder: '', existingFilePolicy: 'ask', verificationAlgorithm: 'sha256',
  verificationExecutionMode: 'afterCopy', isPriority: false,
};

export function App() {
  const [session, setSession] = useState<Session>();
  const [folders, setFolders] = useState<AuthorizedFolder[]>([]);
  const [tasks, setTasks] = useState<ClipPortTask[]>([]);
  const [settings, setSettings] = useState<AppSettings>();
  const [form, setForm] = useState<TaskForm>(initialForm);
  const [language, setLanguage] = useState<Language>('zh-CN');
  const [systemTheme, setSystemTheme] = useState<Theme>('light');
  const [view, setView] = useState<View>('new');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [details, setDetails] = useState<ClipPortTask>();
  const [selectedTasks, setSelectedTasks] = useState<Set<string>>(new Set());
  const [duplicateDecisions, setDuplicateDecisions] = useState<Record<string, ExistingPolicy>>({});
  const [failureSelection, setFailureSelection] = useState<Set<string>>(new Set());
  const [update, setUpdate] = useState<UpdateMetadata>();
  const t = useMemo(() => translator(language), [language]);

  const refreshFolders = useCallback(async () => setFolders(await loadFolders()), []);
  const perform = useCallback(async (operation: () => Promise<unknown>) => {
    setBusy(true); setError(''); setNotice('');
    try { await operation(); }
    catch (caught) {
      setError(caught instanceof ApiError || caught instanceof Error ? caught.message : t('operationFailed'));
    } finally { setBusy(false); }
  }, [t]);

  const applySettings = useCallback((next: AppSettings, platformTheme: Theme) => {
    const nextLanguage = settingLanguage(next.language);
    setSettings(next);
    setLanguage(nextLanguage);
    document.documentElement.lang = nextLanguage;
    document.documentElement.dataset.theme = next.theme === 'system' ? platformTheme : next.theme;
    document.documentElement.dataset.accent = next.accent;
  }, []);

  useEffect(() => {
    let cancelled = false;
    void Promise.all([loadSession(), loadFolders(), loadTasks(), fnosSdk.getPlatformConfig(), loadSettings()])
      .then(([loadedSession, loadedFolders, loadedTasks, platform, loadedSettings]) => {
        if (cancelled) return;
        setSession(loadedSession); setFolders(loadedFolders); setTasks(loadedTasks);
        setSystemTheme(platform.theme); applySettings(loadedSettings, platform.theme);
        void fnosSdk.setTitle('ClipPort');
        void fnosSdk.listen(nextTheme => {
          setSystemTheme(nextTheme);
          setSettings(current => {
            if (current?.theme === 'system') document.documentElement.dataset.theme = nextTheme;
            return current;
          });
        }, nextLanguage => {
          setSettings(current => {
            if (!current) {
              setLanguage(nextLanguage); document.documentElement.lang = nextLanguage;
            }
            return current;
          });
        });
      })
      .catch(caught => setError(caught instanceof Error ? caught.message : t('operationFailed')));
    return () => { cancelled = true; };
  }, [applySettings, t]);

  useEffect(() => {
    let stopped = false; let reconnectTimer = 0; let socket: WebSocket | undefined;
    const connect = () => {
      socket = new WebSocket(websocketUrl());
      socket.onmessage = event => {
        const message = JSON.parse(event.data as string) as { type: string; data: unknown };
        if (message.type === 'snapshot') { setTasks(message.data as ClipPortTask[]); return; }
        if (message.type === 'taskDeleted') {
          const { id } = message.data as { id: string };
          setTasks(current => current.filter(item => item.id !== id)); return;
        }
        if (message.type === 'progress') {
          const progress = message.data as Partial<ClipPortTask> & { id: string; progress: ClipPortTask['progress'] };
          setTasks(current => current.map(item => item.id === progress.id ? { ...item, ...progress } : item));
          return;
        }
        const changed = message.data as ClipPortTask;
        if (changed?.id) setTasks(current => current.some(item => item.id === changed.id)
          ? current.map(item => item.id === changed.id ? changed : item)
          : [changed, ...current]);
      };
      socket.onclose = () => { if (!stopped) reconnectTimer = window.setTimeout(connect, 1500); };
    };
    connect();
    return () => { stopped = true; window.clearTimeout(reconnectTimer); socket?.close(); };
  }, []);

  useEffect(() => {
    const onAuthResult = (event: MessageEvent) => {
      if (event.origin !== window.location.origin || event.data?.type !== 'clipport:auth-result') return;
      const expected = sessionStorage.getItem('clipport-auth-state');
      if (!expected || event.data.result?.state !== expected) return;
      sessionStorage.removeItem('clipport-auth-state'); void perform(refreshFolders);
    };
    window.addEventListener('message', onAuthResult);
    return () => window.removeEventListener('message', onAuthResult);
  }, [perform, refreshFolders]);

  const chooseFolder = async (kind: 'source' | 'destination') => {
    setBusy(true); setError(''); setNotice('');
    try {
      const selected = (await fnosSdk.pickFolder(kind)).at(-1);
      if (!selected) return;
      setForm(current => ({
        ...current,
        [kind === 'source' ? 'sourcePath' : 'destinationPath']: selected,
        destinationSubfolder: kind === 'source' && current.enableCopy && !current.destinationSubfolder
          ? safeSubfolderName(selected) : current.destinationSubfolder,
      }));
      try { await refreshFolders(); }
      catch { setNotice(t('folderSelectedSyncFailed')); }
    } catch (caught) {
      const message = caught instanceof Error ? caught.message : t('operationFailed');
      setError(`${t('nativePickerFailed')}：${message}`);
    } finally { setBusy(false); }
  };

  const onCreate = async (event: React.FormEvent) => {
    event.preventDefault();
    await perform(async () => {
      const mode = !form.enableCopy ? 'verifyOnly' : form.verifyFiles ? 'copyAndVerify' : 'copyOnly';
      const task = await createTask({
        mode, sourcePath: form.sourcePath, destinationPath: form.destinationPath,
        destinationSubfolder: form.enableCopy ? form.destinationSubfolder : '',
        existingFilePolicy: form.enableCopy ? form.existingFilePolicy : 'overwrite',
        verificationAlgorithm: form.verificationAlgorithm,
        verificationExecutionMode: form.enableCopy && form.verifyFiles
          ? form.verificationExecutionMode : 'afterCopy',
        isPriority: form.isPriority,
      });
      setTasks(current => [task, ...current.filter(item => item.id !== task.id)]);
      setForm(current => ({ ...current, destinationSubfolder: '' })); setView('running');
    });
  };

  const act = (task: ClipPortTask, action: 'pause' | 'resume' | 'cancel' | 'restart' | 'verify') =>
    perform(async () => {
      const result = await taskAction(task.id, action);
      if (result && 'id' in result) setTasks(current => [result, ...current.filter(item => item.id !== result.id)]);
    });

  const locate = (path: string) => perform(async () => {
    await validateFolder(path); await fnosSdk.openFileManager(path);
  });

  const chooseReportDirectory = async () => {
    const selected = (await fnosSdk.pickFolder('destination')).at(-1);
    if (selected) await refreshFolders().catch(() => setNotice(t('folderSelectedSyncFailed')));
    return selected;
  };

  const exportSelected = () => perform(async () => {
    let destination = settings?.reportExportDirectory ?? undefined;
    if (!destination) destination = await chooseReportDirectory();
    if (!destination) return;
    const result = await exportReports([...selectedTasks], destination);
    setNotice(`${t('exportSucceeded')}：${result.exportedCount}`);
  });

  const duplicateTask = tasks.find(item => item.status === 'awaitingDuplicateDecision');
  const failureTask = tasks.find(item => item.status === 'awaitingFailureDecision');
  useEffect(() => {
    setFailureSelection(new Set(failureTask?.failedFiles.map(file => file.relativePath) ?? []));
  }, [failureTask]);

  const sourceFolders = folders.filter(folder => folder.readable);
  const destinationFolders = folders.filter(folder => form.enableCopy ? folder.writable : folder.readable);
  const activeTasks = tasks.filter(task => activeStatuses.has(task.status));
  const historyTasks = tasks.filter(task => terminalStatuses.has(task.status));

  return <div className="app-shell">
    <header className="topbar"><div className="brand"><img src="./icon.png" alt="" /><div><h1>ClipPort</h1><p>{t('appSubtitle')}</p></div></div>
      {session && <span className="user-chip">{t('signedInAs')} · {session.username}</span>}</header>
    <nav className="app-nav" aria-label="ClipPort">
      {(['new', 'running', 'history', 'authorization', 'settings', 'about'] as const).map(item =>
        <button key={item} className={view === item ? 'active' : ''} onClick={() => setView(item)}>{t(item === 'new' ? 'newTask' : item === 'running' ? 'runningTasks' : item)}</button>)}
    </nav>
    {!session?.isCompatible && session && <div className="banner danger">{t('compatibleError')}</div>}
    {notice && <div className="banner warning">{notice}<button onClick={() => setNotice('')}>×</button></div>}
    {error && <div className="banner danger"><strong>{t('operationFailed')}：</strong>{error}<button onClick={() => setError('')}>×</button></div>}

    <main className="view-workspace">
      {view === 'new' && <section className="panel composer full-composer">
        <div className="section-heading"><div><span className="eyebrow">CLIPPORT</span><h2>{t('newTask')}</h2></div><button className="ghost" disabled={busy} onClick={() => void perform(refreshFolders)}>↻ {t('refreshAuth')}</button></div>
        {fnosSdk.isStandaloneWeb && <p className="hint">{t('browserAuthHint')}</p>}
        <form onSubmit={event => void onCreate(event)}>
          <div className="folder-grid">
            <FolderField label={t('source')} value={form.sourcePath} folders={sourceFolders} emptyText={t('noAuthorizedFolders')} buttonText={t('chooseSource')} onChange={sourcePath => setForm(current => ({ ...current, sourcePath }))} onChoose={() => void chooseFolder('source')} />
            <div className="flow-arrow" aria-hidden="true">→</div>
            <FolderField label={t('destination')} value={form.destinationPath} folders={destinationFolders} emptyText={t('noAuthorizedFolders')} buttonText={t('chooseDestination')} onChange={destinationPath => setForm(current => ({ ...current, destinationPath }))} onChoose={() => void chooseFolder('destination')} />
          </div>
          <div className="toggle-row mode-toggles">
            <label><input type="checkbox" checked={form.enableCopy} onChange={event => setForm(current => event.target.checked
              ? { ...current, enableCopy: true }
              : { ...current, enableCopy: false, verifyFiles: true, destinationSubfolder: '', existingFilePolicy: 'ask', verificationExecutionMode: 'afterCopy' })} />{t('enableCopy')}</label>
            <label><input type="checkbox" checked={form.verifyFiles} onChange={event => setForm(current => event.target.checked
              ? { ...current, verifyFiles: true }
              : { ...current, enableCopy: true, verifyFiles: false, verificationExecutionMode: 'afterCopy' })} />{t('verifyFiles')}</label>
            <label><input type="checkbox" checked={form.isPriority} onChange={event => setForm(current => ({ ...current, isPriority: event.target.checked }))} />{t('priority')}</label>
          </div>
          <div className="form-grid">
            <label>{t('subfolder')}<input disabled={!form.enableCopy} value={form.destinationSubfolder} onChange={event => setForm(current => ({ ...current, destinationSubfolder: event.target.value }))} /></label>
            <label>{t('duplicate')}<select disabled={!form.enableCopy} value={form.existingFilePolicy} onChange={event => setForm(current => ({ ...current, existingFilePolicy: event.target.value as ExistingPolicy }))}>
              <option value="ask">{t('ask')}</option><option value="overwrite">{t('overwrite')}</option><option value="skip">{t('skip')}</option><option value="createCopy">{t('createCopy')}</option>
            </select></label>
            <label>{t('algorithm')}<select disabled={!form.verifyFiles} value={form.verificationAlgorithm} onChange={event => setForm(current => ({ ...current, verificationAlgorithm: event.target.value as HashAlgorithm }))}>
              {['sha256', 'sha512', 'sha1', 'md5', 'xxHash64'].map(algorithm => <option key={algorithm} value={algorithm}>{algorithm.toUpperCase()}</option>)}
            </select></label>
            <label>{t('verifyTiming')}<select disabled={!(form.enableCopy && form.verifyFiles)} value={form.verificationExecutionMode} onChange={event => setForm(current => ({ ...current, verificationExecutionMode: event.target.value as TaskForm['verificationExecutionMode'] }))}>
              <option value="afterCopy">{t('afterCopy')}</option><option value="opportunisticDuringCopy">{t('opportunisticDuringCopy')}</option>
            </select></label>
          </div>
          <button className="primary create-button" disabled={busy || !session?.isCompatible || !form.sourcePath || !form.destinationPath}>{t('create')}</button>
        </form>
      </section>}

      {(view === 'running' || view === 'history') && <TaskListPanel
        title={view === 'running' ? t('runningTasks') : t('history')}
        emptyText={view === 'running' ? t('activeTasksEmpty') : t('historyEmpty')}
        tasks={view === 'running' ? activeTasks : historyTasks} language={language}
        selected={selectedTasks} onToggle={id => setSelectedTasks(current => toggleSet(current, id))}
        onDetails={setDetails} onAction={act} onDelete={task => void perform(async () => { await deleteTask(task.id); setTasks(current => current.filter(item => item.id !== task.id)); })}
        onBatchDelete={() => void perform(async () => { await deleteTasks([...selectedTasks]); setTasks(current => current.filter(item => !selectedTasks.has(item.id))); setSelectedTasks(new Set()); })}
        onBatchReports={() => void exportSelected()} />}

      {view === 'authorization' && <AuthorizationPanel folders={folders} language={language} busy={busy}
        onRefresh={() => void perform(refreshFolders)} onAdd={() => void perform(async () => { await fnosSdk.pickFolder('source'); await refreshFolders(); })}
        onRevoke={folder => void perform(async () => { await revokeFolder(folder.path); await refreshFolders(); })}
        onLocate={folder => void locate(folder.path)} />}

      {view === 'settings' && settings && <SettingsPanel value={settings} language={language}
        onChooseReportDirectory={chooseReportDirectory}
        onSave={async next => { const saved = await saveSettings(next); applySettings(saved, systemTheme); }} />}

      {view === 'about' && <AboutPanel language={language} update={update} busy={busy}
        onCheck={() => void perform(async () => setUpdate(await checkUpdate()))}
        onOpen={url => void fnosSdk.openUrl(url)} onAppCenter={() => void fnosSdk.openApplicationSettings()} />}
    </main>

    {details && <DetailsDialog task={details} language={language} onClose={() => setDetails(undefined)} onLocate={path => void locate(path)} />}
    {duplicateTask && <DecisionDialog title={t('waitingDuplicate')}>
      <div className="dialog-actions batch-decisions"><button onClick={() => setDuplicateDecisions(Object.fromEntries(duplicateTask.duplicateFiles.map(file => [file.relativePath, 'overwrite'])))}>{t('applyAllOverwrite')}</button><button onClick={() => setDuplicateDecisions(Object.fromEntries(duplicateTask.duplicateFiles.map(file => [file.relativePath, 'skip'])))}>{t('applyAllSkip')}</button><button onClick={() => setDuplicateDecisions(Object.fromEntries(duplicateTask.duplicateFiles.map(file => [file.relativePath, 'createCopy'])))}>{t('applyAllCopy')}</button></div>
      <div className="decision-list">{duplicateTask.duplicateFiles.map(file => <label key={file.relativePath}><span>{file.relativePath}</span><select value={duplicateDecisions[file.relativePath] ?? 'skip'} onChange={event => setDuplicateDecisions(current => ({ ...current, [file.relativePath]: event.target.value as ExistingPolicy }))}><option value="overwrite">{t('overwrite')}</option><option value="skip">{t('skip')}</option><option value="createCopy">{t('createCopy')}</option></select></label>)}</div>
      <button className="primary" onClick={() => void perform(async () => { await submitDuplicates(duplicateTask.id, duplicateTask.duplicateFiles.map(file => ({ relativePath: file.relativePath, decision: duplicateDecisions[file.relativePath] ?? 'skip' }))); setDuplicateDecisions({}); })}>{t('confirmDecisions')}</button>
    </DecisionDialog>}
    {failureTask && <DecisionDialog title={t('waitingFailure')}>
      <p>{t('chooseFailedItems')}</p><div className="failure-list">{failureTask.failedFiles.map(file => <label key={file.relativePath}><input type="checkbox" checked={failureSelection.has(file.relativePath)} onChange={() => setFailureSelection(current => toggleSet(current, file.relativePath))} /><span><strong>{file.relativePath}</strong><small>{file.error}</small></span></label>)}</div>
      <div className="dialog-actions"><button onClick={() => void perform(() => submitFailures(failureTask.id, 'skip', [...failureSelection]))}>{t('skip')}</button><button onClick={() => void perform(() => submitFailures(failureTask.id, 'retry', [...failureSelection]))}>{t('retry')}</button>{failureTask.failedFiles.filter(file => failureSelection.has(file.relativePath)).every(file => file.isVerificationMismatch) && <button className="primary" onClick={() => void perform(() => submitFailures(failureTask.id, 'overwrite', [...failureSelection]))}>{t('markOverwrite')}</button>}</div>
    </DecisionDialog>}
  </div>;
}

function FolderField(props: { label: string; value: string; folders: AuthorizedFolder[]; emptyText: string; buttonText: string; onChange(value: string): void; onChoose(): void }) {
  return <fieldset className="folder-field"><legend>{props.label}</legend><select value={props.value} onChange={event => props.onChange(event.target.value)}><option value="">{props.emptyText}</option>{props.value && !props.folders.some(folder => folder.path === props.value) && <option value={props.value}>{props.value}</option>}{props.folders.map(folder => <option key={folder.path} value={folder.path}>{folder.semanticPath}</option>)}</select><button type="button" onClick={props.onChoose}>{props.buttonText}</button></fieldset>;
}

function TaskListPanel(props: { title: string; emptyText: string; tasks: ClipPortTask[]; language: Language; selected: Set<string>; onToggle(id: string): void; onDetails(task: ClipPortTask): void; onAction(task: ClipPortTask, action: 'pause' | 'resume' | 'cancel' | 'restart' | 'verify'): void; onDelete(task: ClipPortTask): void; onBatchDelete(): void; onBatchReports(): void }) {
  const t = translator(props.language);
  return <section className="panel task-section full-panel"><div className="section-heading"><div><span className="eyebrow">FIFO · PRIORITY</span><h2>{props.title}</h2></div><span className="count">{props.tasks.length}</span></div>
    {props.tasks.some(task => terminalStatuses.has(task.status)) && <div className="batch-toolbar"><span>{props.selected.size} {t('selected')}</span><button disabled={!props.selected.size} onClick={props.onBatchReports}>{t('batchReports')}</button><button className="danger-text" disabled={!props.selected.size} onClick={props.onBatchDelete}>{t('batchDelete')}</button></div>}
    <div className="task-list">{props.tasks.length === 0 && <div className="empty-state"><div>⇄</div><p>{props.emptyText}</p></div>}{props.tasks.map(task => <TaskCard key={task.id} task={task} language={props.language} selected={props.selected.has(task.id)} onToggle={() => props.onToggle(task.id)} onDetails={() => props.onDetails(task)} onAction={action => props.onAction(task, action)} onDelete={() => props.onDelete(task)} />)}</div>
  </section>;
}

function TaskCard({ task, language, selected, onToggle, onDetails, onAction, onDelete }: { task: ClipPortTask; language: Language; selected: boolean; onToggle(): void; onDetails(): void; onAction(action: 'pause' | 'resume' | 'cancel' | 'restart' | 'verify'): void; onDelete(): void }) {
  const t = translator(language); const progress = task.progress; const ratio = progress?.isTotalKnown && progress.totalBytes > 0 ? Math.min(100, progress.processedBytes / progress.totalBytes * 100) : 0;
  return <article className="task-card"><div className="task-main">{terminalStatuses.has(task.status) ? <input aria-label={t('selected')} type="checkbox" checked={selected} onChange={onToggle} /> : <div className={`status-dot ${task.status}`} />}<div className="task-copy"><div className="task-title"><strong>{task.displayName || task.request.sourcePath}</strong>{task.request.isPriority && <span className="priority-pill">{t('priority')}</span>}<span className="status-pill">{statusLabel(task.status, language)}</span></div><p title={`${task.request.sourcePath} → ${task.request.destinationPath}`}>{task.request.sourcePath} <b>→</b> {task.request.destinationPath}</p>{progress && !terminalStatuses.has(task.status) && <><div className="progress-track"><span style={{ width: `${ratio}%` }} /></div><small>{progress.currentFile || progress.phase} · {progress.processedFiles}/{progress.totalFiles} {t('files')}</small></>}</div></div>
    <div className="task-actions"><button onClick={onDetails}>{t('details')}</button>{task.status === 'running' && <button onClick={() => onAction('pause')}>{t('pause')}</button>}{task.status === 'paused' && <button onClick={() => onAction('resume')}>{t('resume')}</button>}{!terminalStatuses.has(task.status) && <button className="danger-text" onClick={() => onAction('cancel')}>{t('cancel')}</button>}{terminalStatuses.has(task.status) && <button onClick={() => onAction('restart')}>{t('restart')}</button>}{terminalStatuses.has(task.status) && task.request.mode !== 'copyOnly' && <button onClick={() => onAction('verify')}>{t('verifyAgain')}</button>}{task.reportFileName && <a className="button-link" href={reportUrl(task.id)}>{t('report')}</a>}{terminalStatuses.has(task.status) && <button className="danger-text" onClick={onDelete}>{t('remove')}</button>}</div></article>;
}

function AuthorizationPanel({ folders, language, busy, onRefresh, onAdd, onRevoke, onLocate }: { folders: AuthorizedFolder[]; language: Language; busy: boolean; onRefresh(): void; onAdd(): void; onRevoke(folder: AuthorizedFolder): void; onLocate(folder: AuthorizedFolder): void }) {
  const t = translator(language); return <section className="panel full-panel authorization-panel"><div className="section-heading"><div><span className="eyebrow">FNOS SHARED ACCESS</span><h2>{t('authorization')}</h2></div><div className="heading-actions"><button disabled={busy} onClick={onRefresh}>↻ {t('refreshAuth')}</button><button className="primary" disabled={busy} onClick={onAdd}>{t('addAuthorization')}</button></div></div><p className="hint">{t('authorizationHint')}</p><div className="authorization-list">{folders.map(folder => <article key={folder.path}><div><strong>{folder.semanticPath}</strong><small>{folder.path}</small><span>{t('readable')} · {folder.writable ? t('writable') : t('notWritable')}</span></div><div><button onClick={() => onLocate(folder)}>{t('locate')}</button><button className="danger-text" onClick={() => onRevoke(folder)}>{t('revokeAuthorization')}</button></div></article>)}</div></section>;
}

function DetailsDialog({ task, language, onClose, onLocate }: { task: ClipPortTask; language: Language; onClose(): void; onLocate(path: string): void }) {
  const t = translator(language); return <DecisionDialog title={task.displayName} onClose={onClose}><dl className="details-grid"><dt>{t('status')}</dt><dd>{statusLabel(task.status, language)}</dd><dt>{t('source')}</dt><dd>{task.request.sourcePath} <button onClick={() => onLocate(task.request.sourcePath)}>{t('locate')}</button></dd><dt>{t('destination')}</dt><dd>{task.request.destinationPath} <button onClick={() => onLocate(task.request.destinationPath)}>{t('locate')}</button></dd><dt>{t('mode')}</dt><dd>{t(task.request.mode)}</dd><dt>{t('algorithm')}</dt><dd>{task.request.verificationAlgorithm.toUpperCase()}</dd></dl><div className="waveform-grid"><Waveform title={t('copyWaveform')} byteRates={task.copyByteSpeedSamples} itemRates={task.copyItemSpeedSamples} positions={task.copyThroughputProgressSamples} emptyText={t('noWaveform')} /><Waveform title={t('verifyWaveform')} byteRates={task.verifyByteSpeedSamples} itemRates={task.verifyItemSpeedSamples} positions={task.verifyThroughputProgressSamples} emptyText={t('noWaveform')} /></div>{task.warnings.length > 0 && <ul>{task.warnings.map(value => <li key={value}>{value}</li>)}</ul>}{task.errors.length > 0 && <ul className="errors">{task.errors.map(value => <li key={value}>{value}</li>)}</ul>}</DecisionDialog>;
}

function AboutPanel({ language, update, busy, onCheck, onOpen, onAppCenter }: { language: Language; update?: UpdateMetadata; busy: boolean; onCheck(): void; onOpen(url: string): void; onAppCenter(): void }) {
  const t = translator(language); return <section className="panel full-panel about-panel"><div className="section-heading"><div><span className="eyebrow">CLIPPORT · FNOS</span><h2>{t('about')}</h2></div><button disabled={busy} onClick={onCheck}>{t('checkUpdate')}</button></div><dl className="details-grid"><dt>{t('version')}</dt><dd>{update?.currentVersion ?? '1.0.0-beta'}</dd><dt>{t('repository')}</dt><dd><button onClick={() => onOpen('https://github.com/MEMZ-Edge01/ClipPort')}>MEMZ-Edge01/ClipPort</button></dd>{update?.latestVersion && <><dt>{t('latestVersion')}</dt><dd>{update.latestVersion}</dd></>}</dl>{update && <div className={`update-card ${update.updateAvailable ? 'available' : ''}`}><strong>{update.updateAvailable ? t('updateAvailable') : t('noUpdate')}</strong><p>{t('updateGuide')}</p><div>{update.downloadUrl && <button className="primary" onClick={() => onOpen(update.downloadUrl!)}>{t('downloadFpk')}</button>}<button onClick={onAppCenter}>{t('openAppCenter')}</button></div></div>}</section>;
}

function DecisionDialog({ title, children, onClose }: { title: string; children: React.ReactNode; onClose?: () => void }) {
  return <div className="modal-backdrop" role="presentation"><section className="dialog" role="dialog" aria-modal="true" aria-label={title}><header><h2>{title}</h2>{onClose && <button aria-label="close" onClick={onClose}>×</button>}</header>{children}</section></div>;
}

function safeSubfolderName(path: string) {
  const leaf = path.replace(/[\\/]+$/, '').split(/[\\/]/).at(-1) ?? '';
  const sanitized = leaf.replace(/[\\/:*?"<>|\p{Cc}]/gu, '_').replace(/[. ]+$/g, '').trim();
  return sanitized || 'ClipPort Copy';
}

function toggleSet(current: Set<string>, value: string) {
  const next = new Set(current); if (next.has(value)) next.delete(value); else next.add(value); return next;
}

function settingLanguage(language: AppSettings['language']): Language {
  return language === 'english' ? 'en-US' : language === 'classicalChinese' ? 'lzh' : 'zh-CN';
}

function statusLabel(status: ClipPortTask['status'], language: Language) {
  const labels: Record<ClipPortTask['status'], [string, string, string]> = {
    queued: ['排队中', 'Queued', '候行'], running: ['运行中', 'Running', '行中'], paused: ['已暂停', 'Paused', '已止'],
    awaitingDuplicateDecision: ['等待重复项决定', 'Duplicate decision', '候決重檔'], awaitingFailureDecision: ['等待失败项处理', 'Failure action', '候處敗項'],
    completed: ['已完成', 'Completed', '已成'], completedWithErrors: ['完成但有错误', 'Completed with errors', '成而有誤'], verificationFailed: ['校验失败', 'Verification failed', '驗之未合'],
    failed: ['失败', 'Failed', '未成'], cancelled: ['已取消', 'Cancelled', '已罷'], interrupted: ['已中断', 'Interrupted', '已中斷'],
  };
  return labels[status][language === 'en-US' ? 1 : language === 'lzh' ? 2 : 0];
}
