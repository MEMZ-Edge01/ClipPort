import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Add24Regular, ArrowClockwise24Regular, ArrowLeft24Regular, ChevronDown24Regular,
  ArrowRight24Regular, ArrowSync24Regular, CheckmarkCircle24Regular,
  CheckboxChecked24Regular, Circle24Regular, Clock24Regular, DataUsage24Regular,
  Delete24Regular, Dismiss24Regular, DismissCircle24Regular, DocumentArrowDown24Regular,
  ErrorCircle24Regular, Folder24Regular, FolderArrowRight24Regular, FolderOpen24Regular,
  FolderSearch24Regular, Grid24Regular, Info24Regular, List24Regular, Mail24Regular,
  PaintBrush24Regular, Pause24Regular, Play24Regular, Prohibited24Regular,
  QuestionCircle24Regular, Settings24Regular, Warning24Regular,
} from '@fluentui/react-icons';
import {
  ApiError, checkUpdate, createTask, deleteTask, deleteTasks, exportReports, loadFolders,
  loadSession, loadSettings, loadTasks, reportUrl, revokeFolder, saveSettings,
  submitDuplicates, submitFailures, taskAction, validateFolder, websocketUrl,
} from './api';
import { fnosSdk } from './fnosSdk';
import { translator } from './i18n';
import type { TranslationKey } from './i18n';
import { ThroughputChart } from './Waveform';
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

const authorizationRefreshDelays = [0, 180, 600] as const;
// Development-only visual fixture for screenshot QA; production builds fold this branch away.
const previewDialog = import.meta.env.DEV && new URLSearchParams(window.location.search).get('preview') === 'dialog';

type ContentView = 'task' | 'settings';

export function App() {
  const [session, setSession] = useState<Session>();
  const [folders, setFolders] = useState<AuthorizedFolder[]>([]);
  const [tasks, setTasks] = useState<ClipPortTask[]>([]);
  const [settings, setSettings] = useState<AppSettings>();
  const [form, setForm] = useState<TaskForm>(() => previewDialog
    ? { ...initialForm, sourcePath: '/vol1/source', destinationPath: '/vol1/destination' }
    : initialForm);
  const [dialogForm, setDialogForm] = useState<TaskForm | undefined>(() => previewDialog
    ? { ...initialForm, sourcePath: '/vol1/source', destinationPath: '/vol1/destination' }
    : undefined);
  const [language, setLanguage] = useState<Language>('zh-CN');
  const [systemTheme, setSystemTheme] = useState<Theme>('light');
  const [contentView, setContentView] = useState<ContentView>('task');
  const [selectedTaskId, setSelectedTaskId] = useState<string>();
  const [multiSelectMode, setMultiSelectMode] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [pendingAuthorizationPath, setPendingAuthorizationPath] = useState<string>();
  const [authorizationUnavailable, setAuthorizationUnavailable] = useState(false);
  const [dialogError, setDialogError] = useState('');
  const [selectedTasks, setSelectedTasks] = useState<Set<string>>(new Set());
  const [duplicateDecisions, setDuplicateDecisions] = useState<Record<string, ExistingPolicy>>({});
  const [failureSelection, setFailureSelection] = useState<Set<string>>(new Set());
  const [update, setUpdate] = useState<UpdateMetadata>();
  const t = useMemo(() => translator(language), [language]);

  const refreshFolders = useCallback(async () => {
    try {
      const next = await loadFolders();
      setFolders(next);
      setAuthorizationUnavailable(false);
      return next;
    } catch (caught) {
      setAuthorizationUnavailable(true);
      throw caught;
    }
  }, []);
  const perform = useCallback(async (operation: () => Promise<unknown>) => {
    setBusy(true); setError(''); setNotice('');
    try { await operation(); }
    catch (caught) {
      setError(friendlyErrorMessage(caught, t));
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
    const mediaQuery = window.matchMedia?.('(prefers-color-scheme: dark)');
    const fallbackTheme: Theme = mediaQuery?.matches ? 'dark' : 'light';
    const platformPromise = fnosSdk.getPlatformConfig()
      .catch(() => ({ language: 'zh-CN' as Language, theme: fallbackTheme }));
    const reportStartupError = (caught: unknown) => {
      if (!cancelled) setError(friendlyErrorMessage(caught, translator('zh-CN')));
    };

    void loadSession().then(value => { if (!cancelled) setSession(value); }).catch(reportStartupError);
    // Authorization is an optional startup surface. Keep tasks and settings usable
    // while the fnOS gateway is temporarily unavailable and expose a focused retry.
    void refreshFolders().catch(() => undefined);
    void loadTasks().then(value => { if (!cancelled) setTasks(value); }).catch(reportStartupError);
    void Promise.all([loadSettings(), platformPromise])
      .then(([loadedSettings, platform]) => {
        if (cancelled) return;
        setSystemTheme(platform.theme);
        applySettings(loadedSettings, platform.theme);
      })
      .catch(reportStartupError);
    void fnosSdk.setTitle('ClipPort').catch(() => undefined);
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
    }).catch(() => undefined);
    return () => { cancelled = true; };
  }, [applySettings, refreshFolders]);

  useEffect(() => {
    let stopped = false; let reconnectTimer = 0; let socket: WebSocket | undefined;
    const connect = () => {
      socket = new WebSocket(websocketUrl());
      socket.onmessage = event => {
        const message = JSON.parse(event.data as string) as { type: string; data: unknown };
        if (message.type === 'snapshot') { setTasks(message.data as ClipPortTask[]); return; }
        if (message.type === 'taskDeleted') {
          const { id } = message.data as { id: string };
          setTasks(current => current.filter(item => item.id !== id));
          setSelectedTaskId(current => current === id ? undefined : current);
          return;
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

  const syncSelectedFolder = async (selectedPath: string) => {
    for (const delay of authorizationRefreshDelays) {
      if (delay > 0) await new Promise(resolve => window.setTimeout(resolve, delay));
      try {
        const refreshed = await refreshFolders();
        if (refreshed.some(folder => samePath(folder.path, selectedPath))) {
          setPendingAuthorizationPath(undefined);
          setNotice('');
          return true;
        }
      } catch {
        // The native authorization list can lag behind the picker result.
      }
    }
    setPendingAuthorizationPath(selectedPath);
    setNotice(t('folderSelectedSyncFailed'));
    return false;
  };

  const chooseFolder = async (kind: 'source' | 'destination', target: 'draft' | 'dialog' = 'draft') => {
    setBusy(true); setError(''); setNotice('');
    try {
      const selected = (await fnosSdk.pickFolder(kind)).at(-1);
      if (!selected) return;
      const updateSelectedPath = (current: TaskForm) => ({
          ...current,
          [kind === 'source' ? 'sourcePath' : 'destinationPath']: selected,
          destinationSubfolder: kind === 'source' && current.enableCopy && !current.destinationSubfolder
            ? safeSubfolderName(selected) : current.destinationSubfolder,
        });
      if (target === 'dialog') {
        setDialogForm(current => current ? updateSelectedPath(current) : current);
      } else {
        setForm(updateSelectedPath);
      }
      await syncSelectedFolder(selected);
    } catch (caught) {
      const message = friendlyErrorMessage(caught, t);
      setError(`${t('nativePickerFailed')}：${message}`);
    } finally { setBusy(false); }
  };

  const retryPendingAuthorization = () => {
    if (!pendingAuthorizationPath) return;
    void perform(async () => {
      if (await syncSelectedFolder(pendingAuthorizationPath)) {
        setNotice(t('folderAuthorizationReady'));
      }
    });
  };

  const retryAuthorizationStartup = () => {
    setBusy(true);
    void refreshFolders().catch(() => undefined).finally(() => setBusy(false));
  };

  const openNewTaskDialog = (reset: boolean) => {
    const next = reset ? { ...initialForm } : { ...form };
    if (reset) {
      setForm(next);
      setSelectedTaskId(undefined);
      setMultiSelectMode(false);
    }
    setDialogError('');
    setDialogForm(next);
  };

  const closeNewTaskDialog = () => {
    setDialogError('');
    setDialogForm(undefined);
  };

  const addAuthorization = () => void perform(async () => {
    const selected = (await fnosSdk.pickFolder('source')).at(-1);
    if (selected) await syncSelectedFolder(selected);
  });

  const onCreate = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!dialogForm) return;
    if (!dialogForm.sourcePath || !dialogForm.destinationPath) {
      setDialogError(t('foldersNotConfigured'));
      return;
    }
    await perform(async () => {
      const mode = !dialogForm.enableCopy ? 'verifyOnly' : dialogForm.verifyFiles ? 'copyAndVerify' : 'copyOnly';
      const task = await createTask({
        mode, sourcePath: dialogForm.sourcePath, destinationPath: dialogForm.destinationPath,
        destinationSubfolder: dialogForm.enableCopy ? dialogForm.destinationSubfolder : '',
        existingFilePolicy: dialogForm.enableCopy ? dialogForm.existingFilePolicy : 'overwrite',
        verificationAlgorithm: dialogForm.verificationAlgorithm,
        verificationExecutionMode: dialogForm.enableCopy && dialogForm.verifyFiles
          ? dialogForm.verificationExecutionMode : 'afterCopy',
        isPriority: dialogForm.isPriority,
      });
      setTasks(current => [task, ...current.filter(item => item.id !== task.id)]);
      setForm({ ...dialogForm, destinationSubfolder: '' });
      setSelectedTaskId(task.id);
      closeNewTaskDialog();
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
    if (selected) await syncSelectedFolder(selected);
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

  const activeTasks = tasks.filter(task => activeStatuses.has(task.status));
  const historyTasks = tasks.filter(task => terminalStatuses.has(task.status));
  const selectedTask = tasks.find(item => item.id === selectedTaskId);

  const handleDeleteTask = (task: ClipPortTask) => void perform(async () => {
    await deleteTask(task.id);
    setTasks(current => current.filter(item => item.id !== task.id));
    if (selectedTaskId === task.id) setSelectedTaskId(undefined);
  });

  return <div className="app-shell">
    {/* Title Bar */}
    <header className="title-bar">
      <img className="title-bar-icon" src="./icon.png?v=1" alt="" />
      <span className="title-bar-text">ClipPort-beta</span>
      {session && <span className="title-bar-user">{t('signedInAs')} · {session.username}</span>}
    </header>

    {/* Banners */}
    {!session?.isCompatible && session && <div className="banner banner-danger">{t('compatibleError')}</div>}
    {notice && <div className="banner banner-warning">
      <span>{notice}</span>
      {pendingAuthorizationPath && <button className="banner-action" onClick={retryPendingAuthorization}>{t('retryAuthorizationSync')}</button>}
      <button className="banner-close" aria-label={t('close')} onClick={() => { setNotice(''); setPendingAuthorizationPath(undefined); }}><Dismiss24Regular /></button>
    </div>}
    {authorizationUnavailable && <div className="banner banner-warning">
      <span>{t('authorizationUnavailable')}</span>
      <button className="banner-action" disabled={busy} onClick={retryAuthorizationStartup}>{t('retryAuthorization')}</button>
    </div>}
    {error && <div className="banner banner-danger"><strong>{t('operationFailed')}：</strong>{error}<button className="banner-close" aria-label={t('close')} onClick={() => setError('')}><Dismiss24Regular /></button></div>}

    {/* Main Workspace */}
    {contentView === 'settings' && settings ? (
      <SettingsShell settings={settings} t={t as (key: string) => string}
        folders={folders} busy={busy} update={update}
        onChooseReportDirectory={chooseReportDirectory}
        onSave={async next => {
          // Preview theme/language immediately; persistence is best-effort and must not make the UI feel inert.
          applySettings(next, systemTheme);
          try {
            const saved = await saveSettings(next);
            applySettings(saved, systemTheme);
          } catch (caught) {
            setError(friendlyErrorMessage(caught, t));
          }
        }}
        onRefresh={() => void perform(refreshFolders)}
        onAdd={addAuthorization}
        onRevoke={folder => void perform(async () => { await revokeFolder(folder.path); await refreshFolders(); })}
        onLocate={folder => void locate(folder.path)}
        onCheck={() => void perform(async () => setUpdate(await checkUpdate()))}
        onOpen={url => void fnosSdk.openUrl(url)}
        onAppCenter={() => void fnosSdk.openApplicationSettings()}
        onBack={() => setContentView('task')} />
    ) : (
      <div className="workspace">
        {/* Sidebar */}
        <aside className="sidebar">
          <div className="sidebar-toolbar">
            <div className="sidebar-toolbar-row">
              <button className="btn btn-accent" onClick={() => openNewTaskDialog(true)}>
                <Add24Regular />{t('newTask')}
              </button>
              <button className="btn" onClick={() => setMultiSelectMode(current => !current)}>
                <CheckboxChecked24Regular />{t('multiSelect')}
              </button>
              <button className="btn btn-icon" aria-label={t('settings')} onClick={() => setContentView('settings')}><Settings24Regular /></button>
            </div>
            {multiSelectMode && <div className="batch-panel">
              <span className="batch-panel-count">{selectedTasks.size} {t('selected')}</span>
              <div className="batch-panel-actions">
                <button className="btn btn-danger" disabled={!selectedTasks.size}
                  onClick={() => void perform(async () => {
                    await deleteTasks([...selectedTasks]);
                    setTasks(current => current.filter(item => !selectedTasks.has(item.id)));
                    if (selectedTaskId && selectedTasks.has(selectedTaskId)) setSelectedTaskId(undefined);
                    setSelectedTasks(new Set());
                  })}>{t('batchDelete')}</button>
                <button className="btn" disabled={!selectedTasks.size}
                  onClick={() => void exportSelected()}>{t('batchReports')}</button>
              </div>
            </div>}
          </div>

          {/* New Tasks Section */}
          <div className="sidebar-section">
            <div className="sidebar-section-header">{t('runningTasks')}</div>
            <div className="task-list new-tasks-list">
              {activeTasks.length === 0 && <div className="sidebar-empty">{t('activeTasksEmpty')}</div>}
              {activeTasks.map(task => <SidebarTaskCard key={task.id} task={task} language={language}
                selected={selectedTaskId === task.id} multiSelect={multiSelectMode}
                checked={selectedTasks.has(task.id)}
                onSelect={() => setSelectedTaskId(task.id)}
                onCheck={() => setSelectedTasks(current => toggleSet(current, task.id))} />)}
            </div>
          </div>

          {/* History Section */}
          <div className="sidebar-section" style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
            <div className="sidebar-section-header">{t('history')}</div>
            <div className="task-list history-list">
              {historyTasks.length === 0 && <div className="sidebar-empty">{t('historyEmpty')}</div>}
              {historyTasks.map(task => <SidebarTaskCard key={task.id} task={task} language={language}
                selected={selectedTaskId === task.id} multiSelect={multiSelectMode}
                checked={selectedTasks.has(task.id)}
                onSelect={() => setSelectedTaskId(task.id)}
                onCheck={() => setSelectedTasks(current => toggleSet(current, task.id))} />)}
            </div>
          </div>
        </aside>

        {/* Content Area */}
        <div className="content-scroll">
          <div className="content-area">
            {selectedTask ? <TaskDetailView task={selectedTask} language={language} t={t as (key: string) => string}
              onAction={act} onDelete={handleDeleteTask}
              onLocate={locate} /> : <DraftTaskView form={form} t={t as (key: string) => string}
                onChoose={kind => void chooseFolder(kind)} onConfigure={() => openNewTaskDialog(false)} />}
          </div>
        </div>
      </div>
    )}

    {/* New Task Dialog */}
    {dialogForm && <div className="modal-backdrop" onClick={event => { if (event.target === event.currentTarget) closeNewTaskDialog(); }}>
      <div className="dialog" role="dialog" aria-label={t('newTask')}>
        <div className="dialog-header">
          <h2 className="dialog-title">{t('newTaskDialog')}</h2>
        </div>
        <form className="dialog-body" onSubmit={event => void onCreate(event)}>
          <div className="new-task-form">
            <section className="dialog-path-section">
              <h3><Folder24Regular />{t('dataSource')}</h3>
              <button type="button" className="dialog-path-button" aria-label={t('chooseSource')} onClick={() => void chooseFolder('source', 'dialog')}>
                <FolderOpen24Regular /><span>{dialogForm.sourcePath || t('dialogSourcePlaceholder')}</span>
              </button>
            </section>
            <section className="dialog-path-section">
              <h3><FolderArrowRight24Regular />{t('copySection')}</h3>
              <span className="form-combo-label">{t('destinationCard')}</span>
              <button type="button" className="dialog-path-button" aria-label={t('chooseDestination')} onClick={() => void chooseFolder('destination', 'dialog')}>
                <FolderOpen24Regular /><span>{dialogForm.destinationPath || t('dialogDestinationPlaceholder')}</span>
              </button>
            </section>
            <label className="toggle-switch">
                <input aria-label={t('copyFilesAccessible')} type="checkbox" checked={dialogForm.enableCopy} onChange={event => setDialogForm(current => current && (event.target.checked
                  ? { ...current, enableCopy: true }
                  : { ...current, enableCopy: false, verifyFiles: true, destinationSubfolder: '', existingFilePolicy: 'ask', verificationExecutionMode: 'afterCopy' }))} />
                <span className="toggle-track" />
                <span className="toggle-label">{dialogForm.enableCopy ? t('on') : t('off')}</span>
            </label>
            <div className="form-combo-group">
              <span className="form-combo-label">{t('subfolder')}</span>
              <input className="form-input" disabled={!dialogForm.enableCopy} value={dialogForm.destinationSubfolder}
                onChange={event => setDialogForm(current => current && ({ ...current, destinationSubfolder: event.target.value }))} />
              <span className="form-field-hint">{t('subfolderHint')}</span>
            </div>
            <div className="form-combo-group">
              <span className="form-combo-label">{t('duplicate')}</span>
              <div className="form-radio-group">
                {([['ask', t('ask')], ['overwrite', t('overwrite')], ['skip', t('skip')], ['createCopy', t('createCopy')]] as const).map(([value, label]) =>
                  <label key={value} className="form-radio">
                    <input type="radio" name="duplicatePolicy" disabled={!dialogForm.enableCopy}
                      checked={dialogForm.existingFilePolicy === value}
                      onChange={() => setDialogForm(current => current && ({ ...current, existingFilePolicy: value }))} />
                    {label}
                  </label>)}
              </div>
              <p className="form-field-hint">{t('duplicateHint')}</p>
            </div>
            <span className="dialog-subsection-heading">{t('fileVerification')}</span>
            <label className="toggle-switch">
              <input aria-label={t('verifyFilesAccessible')} type="checkbox" checked={dialogForm.verifyFiles} onChange={event => setDialogForm(current => current && (event.target.checked
                ? { ...current, verifyFiles: true }
                : { ...current, enableCopy: true, verifyFiles: false, verificationExecutionMode: 'afterCopy' }))} />
              <span className="toggle-track" />
              <span className="toggle-label">{dialogForm.verifyFiles ? t('on') : t('off')}</span>
            </label>
            <div className="form-combo-group">
              <label className="toggle-switch">
                <input aria-label={t('opportunisticDuringCopy')} type="checkbox" disabled={!(dialogForm.enableCopy && dialogForm.verifyFiles)}
                  checked={dialogForm.verificationExecutionMode === 'opportunisticDuringCopy'}
                  onChange={event => setDialogForm(current => current && ({ ...current,
                    verificationExecutionMode: event.target.checked ? 'opportunisticDuringCopy' : 'afterCopy' }))} />
                <span className="toggle-track" />
                <span className="toggle-label">{dialogForm.verificationExecutionMode === 'opportunisticDuringCopy' ? t('on') : t('off')}</span>
              </label>
              <span className="form-field-hint">{t('opportunisticHint')}</span>
            </div>
            <div className="form-combo-group">
              <span className="form-combo-label">{t('algorithm')}</span>
              <SelectMenu label={t('algorithm')} value={dialogForm.verificationAlgorithm} disabled={!dialogForm.verifyFiles}
                options={[['sha256', 'SHA-256'], ['sha512', 'SHA-512'], ['sha1', 'SHA-1'], ['md5', 'MD5'], ['xxHash64', 'xxHash64']].map(([value, label]) => ({ value, label }))}
                onChange={value => setDialogForm(current => current && ({ ...current, verificationAlgorithm: value as HashAlgorithm }))} />
              <span className="form-field-hint">{t(verificationAlgorithmHintKey(dialogForm.verificationAlgorithm))}</span>
            </div>
            <label className="toggle-switch priority-toggle">
              <input aria-label={t('priorityAccessible')} type="checkbox" checked={dialogForm.isPriority} onChange={event => setDialogForm(current => current && ({ ...current, isPriority: event.target.checked }))} />
              <span className="toggle-track" />
              <span className="toggle-label">{dialogForm.isPriority ? t('on') : t('off')}</span>
            </label>
            <p className="form-field-hint">{t('priorityHint')}</p>
          </div>
          {dialogError && <div className="dialog-validation-error" role="alert">{dialogError}</div>}
          <div className="dialog-actions" style={{ marginTop: 16 }}>
            <button type="submit" className="btn btn-accent btn-lg" disabled={busy || !session?.isCompatible}>{t('create')}</button>
            <button type="button" className="btn btn-lg" onClick={closeNewTaskDialog}>{t('cancel')}</button>
          </div>
        </form>
      </div>
    </div>}

    {/* Duplicate Decision Dialog */}
    {duplicateTask && <DecisionDialog title={t('waitingDuplicate')}>
      <div style={{ display: 'flex', gap: 8, marginBottom: 12 }}>
        <button className="btn btn-sm" onClick={() => setDuplicateDecisions(Object.fromEntries(duplicateTask.duplicateFiles.map(file => [file.relativePath, 'overwrite'])))}>{t('overwrite')}</button>
        <button className="btn btn-sm" onClick={() => setDuplicateDecisions(Object.fromEntries(duplicateTask.duplicateFiles.map(file => [file.relativePath, 'skip'])))}>{t('skip')}</button>
        <button className="btn btn-sm" onClick={() => setDuplicateDecisions(Object.fromEntries(duplicateTask.duplicateFiles.map(file => [file.relativePath, 'createCopy'])))}>{t('createCopy')}</button>
      </div>
      <div className="duplicate-list" style={{ marginBottom: 16 }}>
        {duplicateTask.duplicateFiles.map(file => <label key={file.relativePath} style={{ display: 'grid', gridTemplateColumns: '1fr 150px', gap: 12, alignItems: 'center', padding: 8, borderBottom: '1px solid var(--border-l1)' }}>
          <span style={{ overflowWrap: 'anywhere' }}>{file.relativePath}</span>
          <select className="form-select" value={duplicateDecisions[file.relativePath] ?? 'skip'}
            onChange={event => setDuplicateDecisions(current => ({ ...current, [file.relativePath]: event.target.value as ExistingPolicy }))}>
            <option value="overwrite">{t('overwrite')}</option><option value="skip">{t('skip')}</option><option value="createCopy">{t('createCopy')}</option>
          </select>
        </label>)}
      </div>
      <div className="dialog-actions">
        <button className="btn btn-accent" onClick={() => void perform(async () => {
          await submitDuplicates(duplicateTask.id, duplicateTask.duplicateFiles.map(file => ({ relativePath: file.relativePath, decision: duplicateDecisions[file.relativePath] ?? 'skip' })));
          setDuplicateDecisions({});
        })}>{t('confirmDecisions')}</button>
      </div>
    </DecisionDialog>}

    {/* Failure Decision Dialog */}
    {failureTask && <DecisionDialog title={t('waitingFailure')}>
      <p style={{ marginBottom: 12, color: 'var(--text-muted)', fontSize: 12 }}>{t('chooseFailedItems')}</p>
      <div className="failed-list" style={{ marginBottom: 16 }}>
        {failureTask.failedFiles.map(file => <label key={file.relativePath} className="failed-item">
          <input type="checkbox" checked={failureSelection.has(file.relativePath)}
            onChange={() => setFailureSelection(current => toggleSet(current, file.relativePath))} />
          <div className="failed-item-info">
            <span className="failed-item-path">{file.relativePath}</span>
            <span className="failed-item-error">{file.error}</span>
          </div>
        </label>)}
      </div>
      <div className="dialog-actions">
        <button className="btn" onClick={() => void perform(() => submitFailures(failureTask.id, 'skip', [...failureSelection]))}>{t('skip')}</button>
        <button className="btn" onClick={() => void perform(() => submitFailures(failureTask.id, 'retry', [...failureSelection]))}>{t('retry')}</button>
        {failureTask.failedFiles.filter(file => failureSelection.has(file.relativePath)).every(file => file.isVerificationMismatch) &&
          <button className="btn btn-accent" onClick={() => void perform(() => submitFailures(failureTask.id, 'overwrite', [...failureSelection]))}>{t('markOverwrite')}</button>}
      </div>
    </DecisionDialog>}
  </div>;
}

/* ── Sidebar Task Card ── */
function SidebarTaskCard({ task, language, selected, multiSelect, checked, onSelect, onCheck }: {
  task: ClipPortTask; language: Language; selected: boolean; multiSelect: boolean; checked: boolean;
  onSelect(): void; onCheck(): void;
}) {
  const t = translator(language);
  const progress = task.progress;
  const meta = progress ? `${formatBytes(progress.totalBytes)} · ${startedTime(task)}` : task.request.sourcePath;
  return <div className={`task-card${selected ? ' selected' : ''}`} onClick={multiSelect ? onCheck : onSelect}>
    <div className="task-card-name">
      {multiSelect && <input type="checkbox" checked={checked} onClick={event => event.stopPropagation()} onChange={onCheck} />}
      <span className="task-card-name-text">{task.displayName || task.request.sourcePath}</span>
      {task.request.isPriority && <span className="priority-badge">{t('priority')}</span>}
    </div>
    <div className="task-card-meta">{meta}</div>
    <div className="task-card-status">
      <span className="task-card-status-icon"><StatusIcon status={task.status} /></span>
      <span className="task-card-status-text">{statusLabel(task.status, language)}</span>
    </div>
  </div>;
}

/* ── New task workspace (matching the default Windows task page) ── */
function DraftTaskView({ form, t, onChoose, onConfigure }: {
  form: TaskForm;
  t: (key: string) => string;
  onChoose(kind: 'source' | 'destination'): void;
  onConfigure(): void;
}) {
  return <>
    <div className="hero-section draft-hero">
      <div className="hero-left">
        <h1 className="hero-title">{t('prepareTask')}</h1>
        <p className="hero-subtitle">{t('choosePathsHint')}</p>
      </div>
      <div className="hero-right">
        <div className="hero-status-row"><span className="hero-status-text">{t('waitingSetup')}</span><span className="hero-percent">0.00%</span></div>
      </div>
    </div>
    <div className="path-cards">
      <button className="path-card path-card-button" onClick={() => onChoose('source')}>
        <span className="path-card-icon"><Folder24Regular /></span>
        <span className="path-card-text"><span className="path-card-label">{t('sourceStorageCard')}</span><span className="path-card-value">{form.sourcePath || t('notSelected')}</span></span>
        <span className="path-card-picker"><FolderOpen24Regular /></span>
      </button>
      <span className="path-arrow"><ArrowRight24Regular /></span>
      <button className="path-card path-card-button" onClick={() => onChoose('destination')}>
        <span className="path-card-icon"><FolderArrowRight24Regular /></span>
        <span className="path-card-text"><span className="path-card-label">{t('destinationCard')}</span><span className="path-card-value">{form.destinationPath || t('notSelected')}</span></span>
        <span className="path-card-picker"><FolderOpen24Regular /></span>
      </button>
    </div>
    <div className="draft-command-row">
      <div className="stats-row">
        {[t('fileSize'), t('fileCount'), t('createdAt'), t('finishedAt'), t('elapsed')].map(label => <div className="stat-item" key={label}><div className="stat-value">--</div><div className="stat-label">{label}</div></div>)}
      </div>
      <button className="btn btn-accent btn-lg draft-start" disabled={!form.sourcePath || !form.destinationPath} onClick={onConfigure}><Play24Regular />{t('startTaskOptions')}</button>
    </div>
    <div className="progress-section">
      <div className="progress-header"><div className="progress-header-left"><Folder24Regular />{t('taskProgress')}</div><div className="progress-header-right">{t('waitingStart')}</div></div>
      <div className="progress-phase-row copy-row"><span className="phase-label copy">{t('copyPhase')}</span><div className="phase-progress-area"><div className="phase-progress" /></div><span className="phase-speed">0 B/s</span><span className="phase-time">00:00:00</span><span className="phase-count">0/0</span></div>
      <div className="progress-phase-row verify-row"><span className="phase-label verify">{t('verifyPhase')}</span><div className="phase-progress-area"><div className="phase-progress" /></div><span className="phase-speed">0 B/s</span><span className="phase-time">00:00:00</span><span className="phase-count">0/0</span></div>
    </div>
    <div className="log-bar"><Info24Regular className="log-bar-icon" /><span className="log-bar-text">{t('readyHint')}</span></div>
    <div className="throughput-section"><div className="throughput-header"><h3>{t('copySpeed')}</h3><button className="btn btn-icon chart-layout-button" aria-label={t('verticalWaveform')}><Grid24Regular /></button></div><div className="throughput-grid"><ThroughputChart title={t('copyByteRate')} color="copy" unit="MB/s" /><ThroughputChart title={t('itemRateTitle')} color="copy" unit="个/s" /></div></div>
  </>;
}

/* ── Task Detail View (content area) ── */
function TaskDetailView({ task, language, t, onAction, onDelete, onLocate }: {
  task: ClipPortTask; language: Language; t: (key: string) => string;
  onAction(task: ClipPortTask, action: 'pause' | 'resume' | 'cancel' | 'restart' | 'verify'): void;
  onDelete(task: ClipPortTask): void;
  onLocate(path: string): void;
}) {
  const progress = task.progress;
  const isRunning = task.status === 'running';
  const isPaused = task.status === 'paused';
  const isTerminal = terminalStatuses.has(task.status);
  const [copyChartsStacked, setCopyChartsStacked] = useState(false);
  const [verifyChartsStacked, setVerifyChartsStacked] = useState(false);
  const hasCopy = task.request.mode !== 'verifyOnly';
  const hasVerify = task.request.mode !== 'copyOnly';
  const copyPercent = progress && hasCopy && progress.totalBytes > 0 ? Math.min(100, (progress.phase === 'copying' || progress.phase === 'completed' ? progress.processedBytes : 0) / progress.totalBytes * 100) : 0;
  const verifyPercent = progress && hasVerify && progress.totalFiles > 0 ? Math.min(100, (progress.phase === 'verifying' || progress.phase === 'completed' ? progress.processedFiles : 0) / progress.totalFiles * 100) : 0;
  const overallPercent = progress?.isTotalKnown && progress.totalBytes > 0 ? Math.min(100, progress.processedBytes / progress.totalBytes * 100) : 0;

  return <>
    {/* Hero Section */}
    <div className="hero-section">
      <div className="hero-left">
        <div className="hero-title-row">
          <h1 className="hero-title">{task.displayName || t('newTask')}</h1>
          {task.request.isPriority && <span className="priority-badge">{t('priority')}</span>}
        </div>
        <p className="hero-subtitle">{progress?.currentFile || task.request.sourcePath}</p>
      </div>
      <div className="hero-right">
        <div className="hero-status-row">
          {isTerminal && task.status === 'completed' && <span className="hero-completion-icon"><CheckmarkCircle24Regular /></span>}
          <span className="hero-status-text">{statusLabel(task.status, language)}</span>
          <span className="hero-percent">{overallPercent.toFixed(2)}%</span>
        </div>
        <div className="hero-actions">
          {isTerminal && task.request.mode !== 'copyOnly' && <button className="btn btn-lg" onClick={() => onAction(task, 'verify')}><CheckmarkCircle24Regular />{t('verifyAgain')}</button>}
          {task.reportFileName && <a className="btn btn-lg" href={reportUrl(task.id)} target="_blank" rel="noopener"><DocumentArrowDown24Regular />{t('report')}</a>}
          {isTerminal && <button className="btn btn-lg" onClick={() => onDelete(task)}><Delete24Regular />{t('remove')}</button>}
          {isTerminal && <button className="btn btn-accent btn-lg" onClick={() => onAction(task, 'restart')}><ArrowClockwise24Regular />{t('restart')}</button>}
          {(isRunning || isPaused) && <button className="btn btn-lg" onClick={() => onAction(task, isRunning ? 'pause' : 'resume')}>
            {isRunning ? <Pause24Regular /> : <Play24Regular />}{isRunning ? t('pause') : t('resume')}
          </button>}
          {!isTerminal && <button className="btn btn-lg btn-danger-text" onClick={() => onAction(task, 'cancel')}><DismissCircle24Regular />{t('cancel')}</button>}
        </div>
      </div>
    </div>

    {/* Path Cards */}
    <div className="path-cards">
      <div className="path-card">
        <div className="path-card-icon"><Folder24Regular /></div>
        <div className="path-card-text">
          <div className="path-card-label">{t('source')}</div>
          <div className="path-card-value">{task.request.sourcePath}</div>
        </div>
        <button className="path-card-picker" aria-label={t('locate')} onClick={() => onLocate(task.request.sourcePath)}><FolderSearch24Regular /></button>
      </div>
      <div className="path-arrow"><ArrowRight24Regular /></div>
      <div className="path-card">
        <div className="path-card-icon"><FolderArrowRight24Regular /></div>
        <div className="path-card-text">
          <div className="path-card-label">{t('destination')}</div>
          <div className="path-card-value">{task.request.destinationPath}</div>
        </div>
        <button className="path-card-picker" aria-label={t('locate')} onClick={() => onLocate(task.request.destinationPath)}><FolderSearch24Regular /></button>
      </div>
    </div>

    {/* Stats Row */}
    <div className="stats-row">
      <div className="stat-item"><div className="stat-value">{progress ? formatBytes(progress.totalBytes) : '--'}</div><div className="stat-label">{t('fileSize')}</div></div>
      <div className="stat-item"><div className="stat-value">{progress ? `${progress.totalFiles}` : '--'}</div><div className="stat-label">{t('fileCount')}</div></div>
      <div className="stat-item"><div className="stat-value">{task.createdAt ? new Date(task.createdAt).toLocaleTimeString() : '--'}</div><div className="stat-label">{t('createdAt')}</div></div>
      <div className="stat-item"><div className="stat-value">{task.finishedAt ? new Date(task.finishedAt).toLocaleTimeString() : '--'}</div><div className="stat-label">{t('finishedAt')}</div></div>
      <div className="stat-item"><div className="stat-value">{progress ? formatDuration(progress.elapsedSeconds) : '--'}</div><div className="stat-label">{t('elapsed')}</div></div>
    </div>

    {/* Progress Section */}
    {progress && <div className="progress-section">
      <div className="progress-header">
        <div className="progress-header-left"><DataUsage24Regular />{t('taskProgress')}</div>
        <div className="progress-header-right">{progressPhaseLabel(progress.phase, language)}</div>
      </div>
      <div className="progress-bar-track"><div className="progress-bar-fill" style={{ width: `${overallPercent}%` }} /></div>
      {hasCopy && <div className="progress-phase-row copy-row">
        <span className="phase-label copy">{t('copyPhase')}</span>
        <div className="phase-progress-area">
          <div className="phase-progress"><div className="phase-progress-fill copy" style={{ width: `${copyPercent}%` }} /></div>
          {copyPercent >= 100 && <span className="phase-completed-badge"><CheckmarkCircle24Regular />{t('completedLabel')}</span>}
        </div>
        <span className="phase-speed">{formatSpeed(progress.bytesPerSecond)}</span>
        <span className="phase-time">{formatDuration(progress.elapsedSeconds)}</span>
        <span className="phase-count">{progress.processedFiles}/{progress.totalFiles}</span>
      </div>}
      {hasVerify && <div className="progress-phase-row verify-row">
        <span className="phase-label verify">{t('verifyPhase')}</span>
        <div className="phase-progress-area">
          <div className="phase-progress"><div className="phase-progress-fill verify" style={{ width: `${verifyPercent}%` }} /></div>
          {verifyPercent >= 100 && <span className="phase-completed-badge verify"><CheckmarkCircle24Regular />{t('completedLabel')}</span>}
        </div>
        <span className="phase-speed">{formatSpeed(progress.bytesPerSecond)}</span>
        <span className="phase-time">{formatDuration(progress.elapsedSeconds)}</span>
        <span className="phase-count">{progress.processedFiles}/{progress.totalFiles}</span>
      </div>}
    </div>}

    {/* Log Bar */}
    <div className="log-bar">
      <Info24Regular className="log-bar-icon" />
      <span className="log-bar-text">{progress?.currentFile || t('readyHint')}</span>
    </div>

    {/* Throughput Charts */}
    <>
      {hasCopy && <div className="throughput-section">
        <div className="throughput-header"><h3>{t('copySpeed')}</h3><button className="btn btn-icon chart-layout-button" aria-label={copyChartsStacked ? t('stackedWaveform') : t('verticalWaveform')} onClick={() => setCopyChartsStacked(value => !value)}>{copyChartsStacked ? <List24Regular /> : <Grid24Regular />}</button></div>
        <div className={`throughput-grid${copyChartsStacked ? ' stacked' : ''}`}>
          <ThroughputChart title={t('copyByteRate')} byteRates={task.copyByteSpeedSamples} positions={task.copyThroughputProgressSamples} color="copy" unit="MB/s" />
          <ThroughputChart title={t('itemRateTitle')} byteRates={task.copyItemSpeedSamples} positions={task.copyThroughputProgressSamples} color="copy" unit="个/s" />
        </div>
      </div>}
      {hasVerify && <div className="throughput-section">
        <div className="throughput-header"><h3>{t('verifySpeed')}</h3><button className="btn btn-icon chart-layout-button" aria-label={verifyChartsStacked ? t('stackedWaveform') : t('verticalWaveform')} onClick={() => setVerifyChartsStacked(value => !value)}>{verifyChartsStacked ? <List24Regular /> : <Grid24Regular />}</button></div>
        <div className={`throughput-grid${verifyChartsStacked ? ' stacked' : ''}`}>
          <ThroughputChart title={t('verifyByteRate')} byteRates={task.verifyByteSpeedSamples} positions={task.verifyThroughputProgressSamples} color="verify" unit="MB/s" />
          <ThroughputChart title={t('itemRateTitle')} byteRates={task.verifyItemSpeedSamples} positions={task.verifyThroughputProgressSamples} color="verify" unit="个/s" />
        </div>
      </div>}
    </>

    {/* Warnings & Errors */}
    {task.warnings.length > 0 && <div style={{ marginTop: 18 }}>
      {task.warnings.map(value => <div key={value} className="banner banner-warning" style={{ margin: '4px 0' }}>{value}</div>)}
    </div>}
    {task.errors.length > 0 && <div style={{ marginTop: 18 }}>
      {task.errors.map(value => <div key={value} className="banner banner-danger" style={{ margin: '4px 0' }}>{value}</div>)}
    </div>}
  </>;
}

/* ── Settings Shell ── */
function SettingsShell({ settings, folders, busy, update, t, onChooseReportDirectory, onSave,
  onRefresh, onAdd, onRevoke, onLocate, onCheck, onOpen, onAppCenter, onBack }: {
  settings: AppSettings; t: (key: string) => string;
  folders: AuthorizedFolder[]; busy: boolean; update?: UpdateMetadata;
  onChooseReportDirectory(): Promise<string | undefined>;
  onSave(value: AppSettings): Promise<void>;
  onRefresh(): void; onAdd(): void; onRevoke(folder: AuthorizedFolder): void; onLocate(folder: AuthorizedFolder): void;
  onCheck(): void; onOpen(url: string): void; onAppCenter(): void;
  onBack(): void;
}) {
  const [section, setSection] = useState<'appearance' | 'general' | 'authorization' | 'notification' | 'about'>('appearance');
  return <div className="settings-shell">
    <nav className="settings-sidebar">
      <div className="settings-sidebar-brand">
        <img className="settings-sidebar-icon" src="./icon.png?v=1" alt="" />
        <div>
          <div className="settings-sidebar-title">ClipPort</div>
          <div className="settings-sidebar-subtitle">{t('settings')}</div>
        </div>
      </div>
      <div className="settings-nav-section-label">{t('settings')}</div>
      <div className="settings-nav-group">
        {([['appearance', t('appearance')], ['general', t('general')], ['authorization', t('authorization')], ['notification', t('notification')], ['about', t('about')]] as const).map(([key, label]) =>
          <button key={key} className={`settings-nav-btn${section === key ? ' active' : ''}`} onClick={() => setSection(key)}>
            <span className="settings-nav-icon" aria-hidden="true"><SettingsSectionIcon section={key} /></span>
            {label}
          </button>)}
      </div>
      <div className="settings-sidebar-back">
        <button className="settings-nav-btn" onClick={onBack}>
          <ArrowLeft24Regular />{t('returnTasks')}
        </button>
      </div>
    </nav>
    <div className="settings-content">
      <div className="settings-content-inner">
        {section === 'appearance' && <AppearanceSection settings={settings} t={t} onSave={onSave} />}
        {section === 'general' && <GeneralSection settings={settings} t={t} onChooseReportDirectory={onChooseReportDirectory} onSave={onSave} />}
        {section === 'authorization' && <AuthorizationSection folders={folders} busy={busy} t={t}
          onRefresh={onRefresh} onAdd={onAdd} onRevoke={onRevoke} onLocate={onLocate} />}
        {section === 'notification' && <NotificationSection settings={settings} t={t} onSave={onSave} />}
        {section === 'about' && <AboutSection update={update} busy={busy} t={t}
          onCheck={onCheck} onOpen={onOpen} onAppCenter={onAppCenter} />}
      </div>
    </div>
  </div>;
}

function AppearanceSection({ settings, t, onSave }: { settings: AppSettings; t: (key: string) => string; onSave(v: AppSettings): Promise<void> }) {
  const [draft, setDraft] = useState(settings);
  useEffect(() => setDraft(settings), [settings]);
  const update = (patch: Partial<AppSettings>) => {
    const next = { ...draft, ...patch };
    setDraft(next);
    void onSave(next);
  };
  return <>
    <h1 className="settings-panel-title">{t('appearance')}</h1>
    <p className="settings-panel-desc">{t('appearanceDescription')}</p>
    <div className="settings-card">
      <div className="settings-card-title">{t('colorModeAndAccent')}</div>
      <div className="settings-row">
        <span className="settings-row-label">{t('colorMode')}</span>
        <select className="form-select" value={draft.theme} onChange={event => update({ theme: event.target.value as AppSettings['theme'] })}>
          <option value="system">{t('system')}</option><option value="light">{t('light')}</option><option value="dark">{t('dark')}</option>
        </select>
      </div>
      <div className="settings-divider" />
      <div className="settings-section-label">{t('themeColor')}</div>
      <div className="accent-picker">
        <button className={`accent-btn accent-btn-system${draft.accent === 'system' ? ' active' : ''}`} onClick={() => update({ accent: 'system' })}>
          <span className="accent-circle" style={{ background: 'var(--accent)' }} />
          <span>{t('systemColor')}</span>
        </button>
        {([['seafoam', '#00B7C3', '海沫绿'], ['brightRose', '#EA005E', '亮玫红'], ['gold', '#FFB900', '黄金色'], ['mint', '#00B294', '浅薄荷'], ['purpleShadow', '#8E8CD8', '紫影色']] as const).map(([key, color, label]) =>
          <button key={key} className={`accent-btn accent-btn-circle${draft.accent === key ? ' active' : ''}`}
            title={label} onClick={() => update({ accent: key })}>
            <span className="accent-circle" style={{ background: color }} />
          </button>)}
      </div>
    </div>
  </>;
}

function GeneralSection({ settings, t, onChooseReportDirectory, onSave }: {
  settings: AppSettings; t: (key: string) => string;
  onChooseReportDirectory(): Promise<string | undefined>;
  onSave(v: AppSettings): Promise<void>;
}) {
  const [draft, setDraft] = useState(settings);
  useEffect(() => setDraft(settings), [settings]);
  const update = (patch: Partial<AppSettings>) => {
    const next = { ...draft, ...patch };
    setDraft(next);
    void onSave(next);
  };
  return <>
    <h1 className="settings-panel-title">{t('general')}</h1>
    <p className="settings-panel-desc">{t('generalDescription')}</p>
    <div className="settings-card">
      <div className="settings-card-title">{t('languageAndFiles')}</div>
      <div className="settings-row">
        <span className="settings-row-label">{t('language')}</span>
        <select className="form-select" value={draft.language} onChange={event => update({ language: event.target.value as AppSettings['language'] })}>
          <option value="simplifiedChinese">{t('simplifiedChinese')}</option><option value="english">English</option><option value="classicalChinese">{t('classicalChinese')}</option>
        </select>
      </div>
      <div className="settings-divider" />
      <div className="settings-section-label">{t('reportExportDirectory')}</div>
      <div style={{ display: 'grid', gridTemplateColumns: '1fr auto', gap: 10, marginTop: 8 }}>
        <input className="form-input" readOnly value={draft.reportExportDirectory ?? ''} />
        <button className="btn" onClick={() => void onChooseReportDirectory().then(path => path && update({ reportExportDirectory: path }))}>{t('chooseReportDirectory')}</button>
      </div>
    </div>
  </>;
}

function NotificationSection({ settings, t, onSave }: {
  settings: AppSettings; t: (key: string) => string;
  onSave(v: AppSettings): Promise<void>;
}) {
  const [draft, setDraft] = useState(settings);
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);
  useEffect(() => setDraft(settings), [settings]);

  const updateChannel = (id: string, patch: Partial<AppSettings['channels'][number]>) =>
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
  const save = async () => { setBusy(true); setMessage(''); try { await onSave(draft); setMessage(t('settingsSaved')); } catch (e) { setMessage(e instanceof Error ? e.message : t('operationFailed')); } finally { setBusy(false); } };

  return <>
    <h1 className="settings-panel-title">{t('notification')}</h1>
    <p className="settings-panel-desc">{t('notificationDescription')}</p>
    <div className="settings-card">
      <div className="settings-card-title">{t('sendScenarios')}</div>
      <div className="form-toggles">
        <label className="toggle-switch">
          <input type="checkbox" checked={draft.notifyOnTaskCompleted} onChange={event => setDraft({ ...draft, notifyOnTaskCompleted: event.target.checked })} />
          <span className="toggle-track" /><span className="toggle-label">{t('notifyCompleted')}</span>
        </label>
        <label className="toggle-switch">
          <input type="checkbox" checked={draft.notifyOnTaskFailed} onChange={event => setDraft({ ...draft, notifyOnTaskFailed: event.target.checked })} />
          <span className="toggle-track" /><span className="toggle-label">{t('notifyFailed')}</span>
        </label>
      </div>
    </div>
    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 18 }}>
      <h2 style={{ fontSize: 18, fontWeight: 600 }}>{t('notificationChannels')}</h2>
      <button className="btn" onClick={addChannel}>{t('addChannel')}</button>
    </div>
    <div className="channel-list">
      {draft.channels.map(channel => <article className="channel-card" key={channel.id}>
        <div className="channel-card-header">
          <label className="toggle-switch">
            <input type="checkbox" checked={channel.isEnabled} onChange={event => updateChannel(channel.id, { isEnabled: event.target.checked })} />
            <span className="toggle-track" /><span className="toggle-label">{t('enabled')}</span>
          </label>
          <button className="btn btn-danger-text" onClick={() => setDraft(current => ({ ...current, channels: current.channels.filter(item => item.id !== channel.id) }))}>{t('deleteChannel')}</button>
        </div>
        <div className="channel-card-fields">
          <div className="channel-card-row">
            <label className="form-combo-label">{t('channelName')}<input className="form-input" value={channel.displayName} onChange={event => updateChannel(channel.id, { displayName: event.target.value })} /></label>
            <label className="form-combo-label">{t('channelKind')}<select className="form-select" value={channel.kind} onChange={event => updateChannel(channel.id, { kind: event.target.value as AppSettings['channels'][number]['kind'] })}>
              {(['weCom', 'dingTalk', 'feishu', 'bark', 'smtp'] as const).map(kind => <option key={kind} value={kind}>{t(kind)}</option>)}
            </select></label>
            <div />
          </div>
          {channel.kind !== 'smtp' ? <label className="form-combo-label">{t('endpoint')}<input className="form-input" type="password" value={channel.endpoint ?? ''} placeholder={channel.hasEndpoint ? t('savedSecret') : ''} onChange={event => updateChannel(channel.id, { endpoint: event.target.value, clearEndpoint: false })} /></label> : <>
            <div className="channel-card-row-2">
              <label className="form-combo-label">{t('smtpHost')}<input className="form-input" value={channel.smtpHost} onChange={event => updateChannel(channel.id, { smtpHost: event.target.value })} /></label>
              <label className="form-combo-label">{t('smtpPort')}<input className="form-input" type="number" value={channel.smtpPort} onChange={event => updateChannel(channel.id, { smtpPort: Number(event.target.value) })} /></label>
            </div>
            <div className="channel-card-row-2">
              <label className="form-combo-label">{t('smtpUsername')}<input className="form-input" value={channel.smtpUsername} onChange={event => updateChannel(channel.id, { smtpUsername: event.target.value })} /></label>
              <label className="form-combo-label">{t('smtpPassword')}<input className="form-input" type="password" value={channel.smtpPassword ?? ''} placeholder={channel.hasSmtpPassword ? t('savedSecret') : ''} onChange={event => updateChannel(channel.id, { smtpPassword: event.target.value, clearSmtpPassword: false })} /></label>
            </div>
            <label className="form-combo-label">{t('smtpFrom')}<input className="form-input" value={channel.smtpFrom} onChange={event => updateChannel(channel.id, { smtpFrom: event.target.value })} /></label>
            <label className="form-combo-label">{t('smtpRecipients')}<input className="form-input" value={channel.smtpRecipients} onChange={event => updateChannel(channel.id, { smtpRecipients: event.target.value })} /></label>
          </>}
        </div>
        <div className="channel-card-footer">
          <button className="btn" onClick={() => void import('./api').then(api => api.testNotification(channel)).then(result => setMessage(result.detail)).catch(error => setMessage(error instanceof Error ? error.message : t('operationFailed')))}>{t('testSend')}</button>
          <span style={{ color: 'var(--text-muted)', fontSize: 12 }}>{message}</span>
        </div>
      </article>)}
    </div>
    <div style={{ display: 'flex', justifyContent: 'flex-end', alignItems: 'center', gap: 12, marginTop: 18 }}>
      <span style={{ color: 'var(--text-muted)', fontSize: 12 }}>{message}</span>
      <button className="btn btn-accent" disabled={busy} onClick={() => void save()}>{t('saveSettings')}</button>
    </div>
  </>;
}

/* ── Authorization settings section ── */
function AuthorizationSection({ folders, busy, t, onRefresh, onAdd, onRevoke, onLocate }: {
  folders: AuthorizedFolder[]; busy: boolean; t: (key: string) => string;
  onRefresh(): void; onAdd(): void; onRevoke(folder: AuthorizedFolder): void; onLocate(folder: AuthorizedFolder): void;
}) {
  return <>
        <h1 className="settings-panel-title">{t('authorization')}</h1>
        <p className="settings-panel-desc">{t('authorizationHint')}</p>
        <div style={{ display: 'flex', gap: 8, marginBottom: 18 }}>
          <button className="btn" disabled={busy} onClick={onRefresh}>{t('refreshAuth')}</button>
          <button className="btn btn-accent" disabled={busy} onClick={onAdd}>{t('addAuthorization')}</button>
        </div>
        <div className="auth-list">
          {folders.length === 0 && <div className="empty-state">{t('noAuthorizedFolders')}</div>}
          {folders.map(folder => <article className="auth-item" key={folder.path}>
            <div className="auth-item-info">
              <strong className="auth-item-path">{folder.semanticPath}</strong>
              <small className="auth-item-raw">{folder.path}</small>
              <span className="auth-item-perm">
                {folder.permissionState === 'unavailable' ? t('permissionUnavailable') : `${t('readable')} · ${folder.writable ? t('writable') : t('notWritable')}`}
                {folder.semanticPathState === 'fallback' ? ` · ${t('semanticPathFallback')}` : ''}
              </span>
            </div>
            <div className="auth-item-actions">
              <button className="btn btn-sm" onClick={() => onLocate(folder)}>{t('locate')}</button>
              <button className="btn btn-sm btn-danger-text" onClick={() => onRevoke(folder)}>{t('revokeAuthorization')}</button>
            </div>
          </article>)}
        </div>
  </>;
}

/* ── About settings section ── */
function AboutSection({ update, busy, t, onCheck, onOpen, onAppCenter }: {
  update?: UpdateMetadata; busy: boolean; t: (key: string) => string;
  onCheck(): void; onOpen(url: string): void; onAppCenter(): void;
}) {
  return <>
        <h1 className="settings-panel-title">{t('about')}</h1>
        <p className="settings-panel-desc">{t('aboutDescription')}</p>
        <div className="settings-card">
          <div className="settings-card-title">{t('applicationInfo')}</div>
          <div className="settings-row"><span className="settings-row-label">{t('version')}</span><span>{update?.currentVersion ?? '1.0.0-beta'}</span></div>
          <div className="settings-divider" />
          <div className="settings-row">
            <span className="settings-row-label">{t('repository')}</span>
            <button className="btn repo-button" onClick={() => onOpen('https://github.com/MEMZ-Edge01/ClipPort')}>
              <img src="./github.svg" alt="" className="repo-button-icon" />
              <span className="repo-button-copy"><strong>MEMZ-Edge01/ClipPort</strong><small>{t('githubOpen')}</small></span>
            </button>
          </div>
        </div>
        <div className="settings-card">
          <div className="settings-card-title">{t('update')}</div>
          <p className="settings-hint">{t('updateDescription')}</p>
          <div className="about-update-actions">
            <button className="btn" disabled={busy} onClick={onCheck}>{t('checkUpdate')}</button>
            {update?.downloadUrl && <button className="btn btn-accent" onClick={() => onOpen(update.downloadUrl!)}>{t('downloadFpk')}</button>}
            {update?.updateAvailable && <button className="btn" onClick={onAppCenter}>{t('openAppCenter')}</button>}
          </div>
          {update && <div className={`update-card${update.updateAvailable ? ' available' : ''}`} style={{ marginTop: 18 }}>
            <strong>{update.updateAvailable ? t('updateAvailable') : t('noUpdate')}</strong>
            <p style={{ marginTop: 8, color: 'var(--text-muted)', fontSize: 12 }}>{t('updateGuide')}</p>
          </div>}
        </div>
  </>;
}

/* ── Decision Dialog ── */
function DecisionDialog({ title, children }: { title: string; children: React.ReactNode }) {
  return <div className="modal-backdrop"><div className="dialog" role="dialog" aria-label={title}>
    <div className="dialog-header"><h2 className="dialog-title">{title}</h2></div>
    <div className="dialog-body">{children}</div>
  </div></div>;
}

function SelectMenu({ label, value, options, disabled, onChange }: {
  label: string;
  value: string;
  options: ReadonlyArray<{ value: string; label: string }>;
  disabled?: boolean;
  onChange(value: string): void;
}) {
  const [open, setOpen] = useState(false);
  const selected = options.find(option => option.value === value) ?? options[0];
  return <div className={`select-menu${open ? ' open' : ''}`}>
    <button type="button" className="select-menu-trigger" role="combobox" aria-label={label}
      aria-expanded={open} disabled={disabled} onClick={() => setOpen(current => !current)}>
      <span>{selected?.label ?? value}</span><ChevronDown24Regular />
    </button>
    {open && <div className="select-menu-list" role="listbox" aria-label={label}>
      {options.map(option => <button type="button" role="option" aria-selected={option.value === value}
        className={`select-menu-option${option.value === value ? ' selected' : ''}`} key={option.value}
        onClick={() => { onChange(option.value); setOpen(false); }}>{option.label}</button>)}
    </div>}
  </div>;
}

/* ── Utility Functions ── */
function safeSubfolderName(path: string) {
  const leaf = path.replace(/[\\/]+$/, '').split(/[\\/]/).at(-1) ?? '';
  const sanitized = leaf.replace(/[\\/:*?"<>|\p{Cc}]/gu, '_').replace(/[. ]+$/g, '').trim();
  return sanitized || 'ClipPort Copy';
}

function samePath(left: string, right: string) {
  const normalize = (value: string) => value.replace(/\\/g, '/').replace(/\/+$/, '');
  return normalize(left) === normalize(right);
}

function verificationAlgorithmHintKey(algorithm: HashAlgorithm) {
  return ({
    sha256: 'algorithmHintSha256',
    sha512: 'algorithmHintSha512',
    sha1: 'algorithmHintSha1',
    md5: 'algorithmHintMd5',
    xxHash64: 'algorithmHintXxHash64',
  } as const)[algorithm];
}

function friendlyErrorMessage(caught: unknown, t: (key: TranslationKey) => string) {
  const message = caught instanceof ApiError || caught instanceof Error ? caught.message.trim() : '';
  if (!message || /^(The request could not be completed\.?|Internal Server Error)$/i.test(message)) {
    return t('requestUnavailable');
  }
  return message;
}

function toggleSet(current: Set<string>, value: string) {
  const next = new Set(current); if (next.has(value)) next.delete(value); else next.add(value); return next;
}

function settingLanguage(language: AppSettings['language']): Language {
  return language === 'english' ? 'en-US' : language === 'classicalChinese' ? 'lzh' : 'zh-CN';
}

function statusLabel(status: ClipPortTask['status'], language: Language) {
  const keys: Record<ClipPortTask['status'], TranslationKey> = {
    queued: 'statusQueued', running: 'statusRunning', paused: 'statusPaused',
    awaitingDuplicateDecision: 'statusAwaitingDuplicate', awaitingFailureDecision: 'statusAwaitingFailure',
    completed: 'statusCompleted', completedWithErrors: 'statusCompletedWithErrors',
    verificationFailed: 'statusVerificationFailed', failed: 'statusFailed',
    cancelled: 'statusCancelled', interrupted: 'statusInterrupted',
  };
  return translator(language)(keys[status]);
}

function StatusIcon({ status }: { status: ClipPortTask['status'] }) {
  switch (status) {
    case 'queued': return <Clock24Regular />;
    case 'running': return <ArrowSync24Regular />;
    case 'paused': return <Pause24Regular />;
    case 'completed': case 'completedWithErrors': return <CheckmarkCircle24Regular />;
    case 'verificationFailed': case 'failed': return <ErrorCircle24Regular />;
    case 'cancelled': return <Prohibited24Regular />;
    case 'interrupted': return <Warning24Regular />;
    case 'awaitingDuplicateDecision': case 'awaitingFailureDecision': return <QuestionCircle24Regular />;
    default: return <Circle24Regular />;
  }
}

function SettingsSectionIcon({ section }: { section: 'appearance' | 'general' | 'authorization' | 'notification' | 'about' }) {
  switch (section) {
    case 'appearance': return <PaintBrush24Regular />;
    case 'general': return <Settings24Regular />;
    case 'authorization': return <FolderOpen24Regular />;
    case 'notification': return <Mail24Regular />;
    case 'about': return <Info24Regular />;
  }
}

function progressPhaseLabel(phase: string, language: Language): string {
  const labels: Record<string, [string, string, string]> = {
    scanning: ['扫描中', 'Scanning', '掃中'],
    copying: ['拷贝中', 'Copying', '抄中'],
    verifying: ['校验中', 'Verifying', '驗中'],
    retryingFailures: ['重试失败项', 'Retrying failures', '重試敗項'],
    completed: ['已完成', 'Completed', '已成'],
  };
  const entry = labels[phase];
  return entry ? entry[language === 'en-US' ? 1 : language === 'lzh' ? 2 : 0] : phase;
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1048576) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1073741824) return `${(bytes / 1048576).toFixed(1)} MB`;
  return `${(bytes / 1073741824).toFixed(2)} GB`;
}

function formatSpeed(bytesPerSecond: number): string {
  if (bytesPerSecond < 1024) return `${bytesPerSecond.toFixed(0)} B/s`;
  if (bytesPerSecond < 1048576) return `${(bytesPerSecond / 1024).toFixed(1)} KB/s`;
  if (bytesPerSecond < 1073741824) return `${(bytesPerSecond / 1048576).toFixed(2)} MB/s`;
  return `${(bytesPerSecond / 1073741824).toFixed(2)} GB/s`;
}

function formatDuration(seconds: number): string {
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = Math.floor(seconds % 60);
  return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
}

function startedTime(task: ClipPortTask): string {
  return task.startedAt ? new Date(task.startedAt).toLocaleString() : task.createdAt ? new Date(task.createdAt).toLocaleString() : '';
}
