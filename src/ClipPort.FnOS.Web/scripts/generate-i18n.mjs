import { readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';

const languages = ['zh-CN', 'en-US', 'lzh'];
// Shared fnOS concepts use the Windows resource identifiers as their single
// source of truth. Platform-only fnOS text stays in i18n.ts.
const sharedResourceKeys = {
  newTask: 'NewJobButtonText.Text',
  newTaskDialog: 'NewTaskDialog.Title',
  prepareTask: 'Info.PrepareNewTask',
  choosePathsHint: 'Info.SelectSourceAndDest',
  sourceStorageCard: 'SourcePathLabel.Text',
  destinationCard: 'DestinationPathLabel.Text',
  fileSize: 'FileSizeLabel.Text',
  fileCount: 'FileCountLabel.Text',
  createdAt: 'CreatedTimeLabel.Text',
  elapsed: 'ElapsedTimeLabel.Text',
  waitingStart: 'Status.WaitingStart',
  copySpeed: 'CopySpeedChartsTitle.Text',
  verifySpeed: 'VerifySpeedChartsTitle.Text',
  itemRateTitle: 'CopyItemRateChartTitle.Text',
  stackedWaveform: 'ThroughputChartsLayout.ShowSideBySide',
  verticalWaveform: 'ThroughputChartsLayout.ShowStacked',
  dataSource: 'DialogSourceLabel.Text',
  copySection: 'DialogCopyLabel.Text',
  dialogSourcePlaceholder: 'DialogSourcePathText.Text',
  dialogDestinationPlaceholder: 'DialogDestinationPathText.Text',
  copyFilesAccessible: 'EnableCopyToggle.Header',
  verifyFilesAccessible: 'VerifyFilesToggle.Header',
  on: 'EnableCopyToggle.OnContent',
  off: 'EnableCopyToggle.OffContent',
  subfolder: 'DestinationSubfolderNameLabel.Text',
  subfolderHint: 'DestinationSubfolderHintText.Text',
  duplicate: 'DuplicateHandlingLabel.Text',
  duplicateHint: 'DuplicateAskHintText.Text',
  ask: 'AskExistingRadio.Content',
  overwrite: 'OverwriteExistingRadio.Content',
  skip: 'SkipExistingRadio.Content',
  createCopy: 'CreateCopyRadio.Content',
  fileVerification: 'VerifyFilesToggle.Header',
  algorithm: 'VerificationAlgorithmLabel.Text',
  opportunisticDuringCopy: 'OpportunisticVerificationToggle.Header',
  opportunisticHint: 'OpportunisticVerificationHintText.Text',
  priorityAccessible: 'PriorityExecutionToggle.Header',
  priority: 'PriorityTaskBadgeText.Text',
  priorityHint: 'PriorityHintText.Text',
  create: 'NewTaskDialog.PrimaryButtonText',
  cancel: 'NewTaskDialog.CloseButtonText',
  foldersNotConfigured: 'Error.SelectSourceAndDest',
  algorithmHintSha256: 'Info.VerificationAlgorithmSha256',
  algorithmHintSha512: 'Info.VerificationAlgorithmSha512',
  algorithmHintSha1: 'Info.VerificationAlgorithmSha1',
  algorithmHintMd5: 'Info.VerificationAlgorithmMd5',
  algorithmHintXxHash64: 'Info.VerificationAlgorithmXxHash64',
  startTaskOptions: 'StartButtonText.Text',
  multiSelect: 'Button.MultiSelect',
  selectAll: 'Button.SelectAll',
  batchDelete: 'Button.BatchDelete',
  batchReports: 'Button.BatchCreateReports',
  pause: 'Button.Pause',
  resume: 'Button.Resume',
  restart: 'Button.Restart',
  verifyAgain: 'Button.Reverify',
  report: 'Button.ExportReport',
  remove: 'Button.DeleteRecord',
  retry: 'Button.RetrySelected',
  statusQueued: 'Status.Queued',
  statusRunning: 'Status.Copying',
  statusPaused: 'Status.Paused',
  statusAwaitingDuplicate: 'Status.WaitingDuplicateChoices',
  statusAwaitingFailure: 'Status.WaitingFailedFiles',
  statusCompleted: 'Result.TaskCompleted',
  statusCompletedWithErrors: 'Result.CompletedWithErrors',
  statusVerificationFailed: 'Error.VerificationFailed',
  statusFailed: 'Result.TaskFailedStatus',
  statusCancelled: 'Error.TaskCancelledKeptShort',
  statusInterrupted: 'Error.AppExitedBeforeFinishShort',
  settings: 'SettingsToolbarButton.ToolTipService.ToolTip',
  appearance: 'Settings.Appearance',
  appearanceDescription: 'Settings.AppearanceDesc',
  colorModeAndAccent: 'Settings.ColorModeAndAccent',
  colorMode: 'Settings.ColorMode',
  language: 'Settings.Language',
  languageAndFiles: 'Settings.LanguageAndFiles',
  notification: 'NotificationTitle.Text',
  notificationDescription: 'NotificationDesc.Text',
  sendScenarios: 'NotificationScenesTitle.Text',
  notificationChannels: 'NotificationChannelsTitle.Text',
  addChannel: 'AddNotificationChannelButton.Content',
  channelName: 'NotificationChannelNameBox.Header',
  channelKind: 'NotificationChannelKindBox.Header',
  enabled: 'NotificationChannelEnabledToggle.Header',
  deleteChannel: 'RemoveNotificationChannelButton.Content',
  testSend: 'TestNotificationChannelButton.Content',
  weCom: 'Notification.ChannelKind.WeCom',
  dingTalk: 'Notification.ChannelKind.DingTalk',
  feishu: 'Notification.ChannelKind.Feishu',
  bark: 'Notification.ChannelKind.Bark',
  smtp: 'Notification.ChannelKind.Smtp',
};
const decode = value => value
  .replaceAll('&lt;', '<')
  .replaceAll('&gt;', '>')
  .replaceAll('&quot;', '"')
  .replaceAll('&apos;', "'")
  .replaceAll('&amp;', '&')
  .replace(/&#(\d+);/g, (_, code) => String.fromCodePoint(Number(code)))
  .replace(/&#x([0-9a-f]+);/gi, (_, code) => String.fromCodePoint(Number.parseInt(code, 16)));

const catalogs = {};
for (const language of languages) {
  const path = resolve(process.cwd(), '..', 'ClipPort', 'Strings', language, 'Resources.resw');
  const xml = await readFile(path, 'utf8');
  const values = {};
  for (const match of xml.matchAll(/<data name="([^"]+)"[^>]*>\s*<value>([\s\S]*?)<\/value>/g)) {
    const key = decode(match[1]);
    if (Object.hasOwn(values, key)) {
      throw new Error(`Duplicate Windows resource key in ${language}: ${key}`);
    }
    values[key] = decode(match[2]);
  }
  catalogs[language] = values;
}

for (const [translationKey, resourceKey] of Object.entries(sharedResourceKeys)) {
  for (const language of languages) {
    if (!Object.hasOwn(catalogs[language], resourceKey)) {
      throw new Error(`Missing Windows resource for ${translationKey} in ${language}: ${resourceKey}`);
    }
  }
}

const output = `// Generated from the Windows .resw catalogs. Do not edit by hand.\n` +
  `export const windowsResources = ${JSON.stringify(catalogs, null, 2)} as const;\n\n` +
  `export const sharedWindowsResourceKeys = ${JSON.stringify(sharedResourceKeys, null, 2)} as const;\n`;
await writeFile(resolve(process.cwd(), 'src', 'generatedTranslations.ts'), output, 'utf8');
