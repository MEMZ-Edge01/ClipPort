import type {
  ApiErrorBody,
  AppSettings,
  AuthorizedFolder,
  ClipPortTask,
  ExistingPolicy,
  HashAlgorithm,
  Session,
  TaskMode,
  UpdateMetadata,
} from './types';

let csrfToken = '';

export class ApiError extends Error {
  constructor(public readonly code: string, message: string) {
    super(message);
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers);
  if (init?.body) {
    headers.set('Content-Type', 'application/json');
  }
  if (csrfToken && init?.method && !['GET', 'HEAD'].includes(init.method)) {
    headers.set('X-ClipPort-CSRF', csrfToken);
  }
  const response = await fetch(`api/v1/${path}`, { ...init, headers });
  if (!response.ok) {
    let error: ApiErrorBody = { code: 'request_failed', message: response.statusText };
    try {
      error = await response.json() as ApiErrorBody;
    } catch {
      // Preserve the stable fallback when a proxy returns a non-JSON error page.
    }
    throw new ApiError(error.code, error.message);
  }
  return response.status === 204 ? undefined as T : await response.json() as T;
}

export async function loadSession(): Promise<Session> {
  const session = await request<Session>('session');
  csrfToken = session.csrfToken;
  return session;
}

export const loadFolders = () => request<AuthorizedFolder[]>('authorized-folders');
export const loadTasks = () => request<ClipPortTask[]>('tasks');

export function createTask(input: {
  mode: TaskMode;
  sourcePath: string;
  destinationPath: string;
  destinationSubfolder: string;
  existingFilePolicy: ExistingPolicy;
  verificationAlgorithm: HashAlgorithm;
  verificationExecutionMode: 'afterCopy' | 'opportunisticDuringCopy';
  isPriority: boolean;
}) {
  return request<ClipPortTask>('tasks', {
    method: 'POST',
    body: JSON.stringify({
      ...input,
      destinationSubfolder: input.destinationSubfolder.trim() || null,
    }),
  });
}

export const taskAction = (id: string, action: 'pause' | 'resume' | 'cancel' | 'restart' | 'verify') =>
  request<ClipPortTask | void>(`tasks/${id}/${action}`, { method: 'POST' });

export const deleteTask = (id: string) =>
  request<void>(`tasks/${id}`, { method: 'DELETE' });

export const deleteTasks = (taskIds: string[]) =>
  request<void>('tasks/batch-delete', {
    method: 'POST',
    body: JSON.stringify({ taskIds }),
  });

export const revokeFolder = (path: string) =>
  request<void>('authorized-folders', {
    method: 'DELETE',
    body: JSON.stringify({ path }),
  });

export const validateFolder = (path: string, requireWrite = false) =>
  request<void>('authorized-folders/validate', {
    method: 'POST',
    body: JSON.stringify({ path, requireWrite }),
  });

export const loadSettings = () => request<AppSettings>('settings');

export const saveSettings = (settings: AppSettings) =>
  request<AppSettings>('settings', {
    method: 'PUT',
    body: JSON.stringify(settings),
  });

export const testNotification = (channel: AppSettings['channels'][number]) =>
  request<{ channelId: string; channelName: string; success: boolean; detail: string }>(
    'settings/notifications/test',
    { method: 'POST', body: JSON.stringify({ channel }) });

export const exportReports = (taskIds: string[], destinationDirectory: string) =>
  request<{ exportedCount: number; fileNames: string[] }>('reports/export', {
    method: 'POST',
    body: JSON.stringify({ taskIds, destinationDirectory }),
  });

export const checkUpdate = () => request<UpdateMetadata>('update');

export const submitDuplicates = (id: string, decisions: { relativePath: string; decision: ExistingPolicy }[]) =>
  request<void>(`tasks/${id}/duplicates`, {
    method: 'POST',
    body: JSON.stringify({ decisions }),
  });

export const submitFailures = (id: string, action: 'retry' | 'overwrite' | 'skip', relativePaths: string[]) =>
  request<void>(`tasks/${id}/failures`, {
    method: 'POST',
    body: JSON.stringify({ action, relativePaths }),
  });

export function reportUrl(id: string) {
  return `api/v1/tasks/${encodeURIComponent(id)}/report`;
}

export function websocketUrl() {
  const url = new URL('ws', window.location.href.endsWith('/') ? window.location.href : `${window.location.href}/`);
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  return url.toString();
}
