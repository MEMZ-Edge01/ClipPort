export type Language = 'zh-CN' | 'en-US' | 'lzh';
export type Theme = 'light' | 'dark';
export type ThemeMode = 'system' | 'light' | 'dark';
export type Accent = 'system' | 'seafoam' | 'brightRose' | 'gold' | 'mint' | 'purpleShadow';

export interface Session {
  isAdmin: boolean;
  userId: number;
  username: string;
  csrfToken: string;
  language: Language;
  systemVersion: string;
  isCompatible: boolean;
}

export interface AuthorizedFolder {
  path: string;
  semanticPath: string;
  readable: boolean;
  writable: boolean;
}

export type TaskMode = 'copyAndVerify' | 'copyOnly' | 'verifyOnly';
export type TaskStatus =
  | 'queued' | 'running' | 'paused'
  | 'awaitingDuplicateDecision' | 'awaitingFailureDecision'
  | 'completed' | 'completedWithErrors' | 'verificationFailed'
  | 'failed' | 'cancelled' | 'interrupted';
export type ExistingPolicy = 'ask' | 'overwrite' | 'skip' | 'createCopy';
export type HashAlgorithm = 'sha256' | 'sha512' | 'sha1' | 'md5' | 'xxHash64';

export interface DuplicateFile {
  relativePath: string;
}

export interface FailedFile {
  relativePath: string;
  error: string;
  isVerificationMismatch: boolean;
}

export interface TaskProgress {
  phase: string;
  totalBytes: number;
  processedBytes: number;
  totalFiles: number;
  processedFiles: number;
  currentFile: string;
  bytesPerSecond: number;
  elapsedSeconds: number;
  isTotalKnown: boolean;
  isPhaseActive: boolean;
}

export interface ClipPortTask {
  id: string;
  displayName: string;
  request: {
    mode: TaskMode;
    sourcePath: string;
    destinationPath: string;
    destinationSubfolder?: string | null;
    existingFilePolicy: ExistingPolicy;
    verificationAlgorithm: HashAlgorithm;
    verificationExecutionMode: 'afterCopy' | 'opportunisticDuringCopy';
    isPriority: boolean;
  };
  status: TaskStatus;
  createdAt: string;
  startedAt?: string | null;
  finishedAt?: string | null;
  progress?: TaskProgress | null;
  duplicateFiles: DuplicateFile[];
  failedFiles: FailedFile[];
  errors: string[];
  warnings: string[];
  reportFileName?: string | null;
  copyByteSpeedSamples: number[];
  copyItemSpeedSamples: number[];
  copyThroughputProgressSamples: number[];
  verifyByteSpeedSamples: number[];
  verifyItemSpeedSamples: number[];
  verifyThroughputProgressSamples: number[];
}

export type NotificationKind = 'weCom' | 'dingTalk' | 'feishu' | 'bark' | 'smtp';

export interface NotificationChannel {
  id: string;
  displayName: string;
  kind: NotificationKind;
  isEnabled: boolean;
  hasEndpoint: boolean;
  endpoint?: string;
  clearEndpoint?: boolean;
  smtpHost: string;
  smtpPort: number;
  smtpUsername: string;
  hasSmtpPassword: boolean;
  smtpPassword?: string;
  clearSmtpPassword?: boolean;
  smtpFrom: string;
  smtpRecipients: string;
}

export interface AppSettings {
  version: number;
  theme: ThemeMode;
  accent: Accent;
  language: 'simplifiedChinese' | 'english' | 'classicalChinese';
  reportExportDirectory?: string | null;
  notifyOnTaskCompleted: boolean;
  notifyOnTaskFailed: boolean;
  channels: NotificationChannel[];
}

export interface UpdateMetadata {
  currentVersion: string;
  latestVersion?: string | null;
  updateAvailable: boolean;
  assetName?: string | null;
  downloadUrl?: string | null;
  releasePageUrl: string;
  publishedAt?: string | null;
}

export interface ApiErrorBody {
  code: string;
  message: string;
  details?: unknown;
}
