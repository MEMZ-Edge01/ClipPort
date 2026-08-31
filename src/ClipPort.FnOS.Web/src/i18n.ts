import { windowsResources } from './generatedTranslations';
import type { Language } from './types';

const zh = {
  appSubtitle: 'NAS 文件复制与完整性校验',
  newTask: '新建任务', newTaskDialog: '新建拷卡任务', runningTasks: '运行任务', history: '历史任务', authorization: '授权目录', settings: '设置', about: '关于',
  source: '源目录', destination: '目标目录', chooseSource: '选择并授权源目录', chooseDestination: '选择并授权目标目录',
  prepareTask: '准备新任务', choosePathsHint: '请选择源目录和目标目录', sourceStorageCard: '源目录 / 存储卡', destinationCard: '拷贝目的地', notSelected: '尚未选择',
  fileSize: '文件大小', fileCount: '文件数', createdAt: '创建时间', finishedAt: '结束时间', elapsed: '累计用时', taskProgress: '任务进度', waitingStart: '等待开始', copyPhase: '拷贝', verifyPhase: '文件校验', readyHint: '就绪。选择目录后即可开始。',
  copySpeed: '拷贝速度', verifySpeed: '校验速度', copyByteRate: '文件拷贝大小速度', verifyByteRate: '文件校验大小速度', itemRateTitle: '项目数速度', stackedWaveform: '并排显示波形', verticalWaveform: '纵向显示波形', completedLabel: '已完成',
  appearanceDescription: '选择应用的明暗外观与强调色。', colorModeAndAccent: '颜色模式与主题色', colorMode: '颜色模式', themeColor: '主题色', systemColor: '系统色', applicationInfo: '应用信息', update: '更新', aboutDescription: '查看版本信息并访问 ClipPort 项目仓库。', updateDescription: '从 GitHub Releases 获取并安装最新版本。', githubOpen: '在 GitHub 中打开', notificationChannels: '通知渠道', priorityHint: '任务会逐个排队执行；勾选优先执行的任务会排到队列最前面。', waitingSetup: '等待设置', generalDescription: '设置界面语言以及日志与报告的默认位置。', languageAndFiles: '语言与文件', notificationDescription: '任务结束后，通过一个或多个外部渠道发送结果。', sendScenarios: '发送场景',
  refreshAuth: '刷新授权目录', noAuthorizedFolders: '尚未授权任何共享目录', folderSelectedSyncFailed: '目录已选择，授权状态仍在同步。', retryAuthorizationSync: '重新同步', folderAuthorizationReady: '目录授权已同步。', nativePickerFailed: '原生目录选择失败',
  mode: '任务模式', copyAndVerify: '复制并校验', copyOnly: '仅复制', verifyOnly: '仅校验', enableCopy: '开启', verifyFiles: '开启', copyFilesAccessible: '复制文件', verifyFilesAccessible: '校验文件', priorityAccessible: '优先', on: '开启', off: '关闭',
  subfolder: '拷贝目的地文件夹名', subfolderHint: '留空则不创建子文件夹', duplicate: '重复项处理', duplicateHint: '询问模式会先继续处理其他文件，再逐个处理检测到的重复文件。', ask: '询问', overwrite: '覆盖', skip: '跳过', createCopy: '创建副本', fileVerification: '文件校验',
  algorithm: '校验算法', verifyTiming: '校验时机', afterCopy: '复制后校验', opportunisticDuringCopy: '拷贝时校验（可用时）', priority: '优先任务',
  create: '创建任务', startTaskOptions: '开始拷卡', tasks: '任务与历史', emptyTasks: '还没有任务', pause: '暂停', resume: '恢复', cancel: '取消', restart: '重新开始', verifyAgain: '再次校验',
  report: '下载报告', remove: '删除历史', details: '详情', progress: '进度', files: '个文件', waitingDuplicate: '需要处理重复文件', waitingFailure: '需要处理失败项',
  retry: '重试', markOverwrite: '覆盖后重新校验', confirmDecisions: '提交决定', close: '关闭', compatibleError: 'fnOS 版本过低，需要 1.2.0401 或更高版本',
  browserAuthHint: '独立浏览器会打开 fnOS 授权窗口，返回后请刷新授权目录', status: '状态', signedInAs: '管理员', language: '语言', theme: '主题',
  system: '跟随系统', light: '浅色', dark: '深色', operationFailed: '操作失败', selected: '已选择', selectAll: '全选', batchDelete: '批量删除', batchReports: '批量导出报告',
  locate: '在文件管理器中定位', addAuthorization: '新增授权', revokeAuthorization: '撤销授权', authorizationHint: '仅显示 fnOS 已授予 ClipPort 且当前管理员可访问的目录。',
  readable: '可读', writable: '可写', notWritable: '只读', activeTasksEmpty: '当前没有运行中的任务', historyEmpty: '当前没有历史任务',
  copyWaveform: '复制吞吐', verifyWaveform: '校验吞吐', byteRate: '字节速率', itemRate: '文件速率', noWaveform: '暂无波形样本',
  appearance: '外观', accent: '强调色', seafoam: '海沫', brightRose: '亮玫瑰', gold: '金色', mint: '薄荷', purpleShadow: '紫影',
  simplifiedChinese: '简体中文', english: 'English', classicalChinese: '文言', general: '常规', saveSettings: '保存设置', settingsSaved: '设置已保存',
  reportExportDirectory: '报告默认导出目录', chooseReportDirectory: '选择报告导出目录', notification: '通知', notifyCompleted: '任务完成时通知', notifyFailed: '任务失败时通知',
  addChannel: '添加通知渠道', channelName: '名称', channelKind: '类型', enabled: '启用', endpoint: 'Webhook / Bark 地址', smtpHost: 'SMTP 主机', smtpPort: '端口',
  smtpUsername: '用户名', smtpPassword: '密码', smtpFrom: '发件人', smtpRecipients: '收件人', savedSecret: '已安全保存；留空则保留', testSend: '测试发送', testSucceeded: '测试发送成功', deleteChannel: '删除渠道',
  weCom: '企业微信', dingTalk: '钉钉', feishu: '飞书', bark: 'Bark', smtp: 'SMTP', version: '版本', repository: '项目仓库', checkUpdate: '检查更新',
  latestVersion: '最新版本', updateAvailable: '发现 fnOS FPK 更新', noUpdate: '当前已是最新版本', downloadFpk: '下载 FPK', openAppCenter: '打开应用设置', updateGuide: '下载对应 x86 FPK 后，请在 fnOS 应用中心完成升级。',
  exportSucceeded: '报告已导出', applyAllOverwrite: '全部覆盖', applyAllSkip: '全部跳过', applyAllCopy: '全部创建副本', chooseFailedItems: '选择要处理的失败项',
  returnTasks: '返回任务', permissionUnavailable: '权限状态暂不可用', semanticPathFallback: '使用原始路径',
} as const;

const platformOverrides: Record<Language, Partial<Record<keyof typeof zh, string>>> = {
  'zh-CN': {},
  'en-US': {
    appSubtitle: 'NAS file copy and integrity verification', newTask: 'New task', newTaskDialog: 'Create card-copy task', startTaskOptions: 'Start task', runningTasks: 'Running', history: 'History', authorization: 'Authorized folders', settings: 'Settings', about: 'About',
    source: 'Source folder', destination: 'Destination folder', chooseSource: 'Select and authorize source', chooseDestination: 'Select and authorize destination', refreshAuth: 'Refresh authorized folders', noAuthorizedFolders: 'No shared folder has been authorized', prepareTask: 'Prepare new task', choosePathsHint: 'Select a source and destination folder', sourceStorageCard: 'Source folder / storage card', destinationCard: 'Copy destination', notSelected: 'Not selected', fileSize: 'File size', fileCount: 'File count', createdAt: 'Created', finishedAt: 'Finished', elapsed: 'Elapsed', taskProgress: 'Task progress', waitingStart: 'Waiting to start', copyPhase: 'Copy', verifyPhase: 'File verification', readyHint: 'Ready. Select folders to begin.', copySpeed: 'Copy speed', verifySpeed: 'Verification speed', copyByteRate: 'File copy byte rate', verifyByteRate: 'File verification byte rate', itemRateTitle: 'Item rate', stackedWaveform: 'Show waveforms side by side', verticalWaveform: 'Show waveforms vertically', completedLabel: 'Completed', appearanceDescription: 'Choose the light/dark appearance and accent color.', colorModeAndAccent: 'Color mode and accent', colorMode: 'Color mode', themeColor: 'Accent color', systemColor: 'System color', applicationInfo: 'Application information', update: 'Updates', aboutDescription: 'View version information and visit the ClipPort repository.', updateDescription: 'Get and install the latest version from GitHub Releases.', githubOpen: 'Open in GitHub', notificationChannels: 'Notification channels', priorityHint: 'Tasks are queued one at a time; priority tasks are placed at the front.', waitingSetup: 'Waiting for setup', generalDescription: 'Configure the interface language and default log/report locations.', languageAndFiles: 'Language and files', notificationDescription: 'Send results through one or more channels when a task ends.', sendScenarios: 'Notification scenarios',
    folderSelectedSyncFailed: 'The folder is selected and its authorization status is still syncing.', retryAuthorizationSync: 'Sync again', folderAuthorizationReady: 'Folder authorization is ready.', nativePickerFailed: 'Native folder selection failed', enableCopy: 'On', verifyFiles: 'On', copyFilesAccessible: 'Copy files', verifyFilesAccessible: 'Verify files', priorityAccessible: 'Priority', on: 'On', off: 'Off', subfolder: 'Destination folder name', subfolderHint: 'Leave blank to avoid creating a subfolder.', duplicate: 'Duplicate handling', duplicateHint: 'Ask mode continues with other files, then handles each detected duplicate.', fileVerification: 'File verification',
    priority: 'Priority task', tasks: 'Tasks and history', emptyTasks: 'No tasks yet', browserAuthHint: 'A fnOS authorization window will open; refresh folders after returning', signedInAs: 'Administrator',
    system: 'Use system', light: 'Light', dark: 'Dark', operationFailed: 'Operation failed', authorizationHint: 'Only folders granted to ClipPort and accessible to the current administrator are shown.', addAuthorization: 'Add authorization', revokeAuthorization: 'Revoke', locate: 'Show in File Manager',
    readable: 'Readable', writable: 'Writable', notWritable: 'Read only', activeTasksEmpty: 'No active tasks', historyEmpty: 'No task history', copyWaveform: 'Copy throughput', verifyWaveform: 'Verification throughput', byteRate: 'Byte rate', itemRate: 'File rate', noWaveform: 'No waveform samples',
    appearance: 'Appearance', accent: 'Accent', general: 'General', saveSettings: 'Save settings', settingsSaved: 'Settings saved', reportExportDirectory: 'Default report export folder', chooseReportDirectory: 'Choose report folder', notification: 'Notifications', notifyCompleted: 'Notify when a task completes', notifyFailed: 'Notify when a task fails',
    addChannel: 'Add channel', channelName: 'Name', channelKind: 'Type', enabled: 'Enabled', endpoint: 'Webhook / Bark URL', smtpHost: 'SMTP host', smtpPort: 'Port', smtpUsername: 'Username', smtpPassword: 'Password', smtpFrom: 'Sender', smtpRecipients: 'Recipients', savedSecret: 'Saved securely; leave blank to keep it', testSend: 'Send test', testSucceeded: 'Test sent', deleteChannel: 'Delete channel',
    version: 'Version', repository: 'Repository', checkUpdate: 'Check for updates', latestVersion: 'Latest version', updateAvailable: 'A fnOS FPK update is available', noUpdate: 'You are up to date', downloadFpk: 'Download FPK', openAppCenter: 'Open app settings', updateGuide: 'Download the matching x86 FPK, then finish the upgrade in fnOS App Center.', exportSucceeded: 'Reports exported', returnTasks: 'Back to tasks', permissionUnavailable: 'Permission status temporarily unavailable', semanticPathFallback: 'Showing raw path',
  },
  lzh: {
    appSubtitle: 'NAS 檔案徙置與驗真', newTask: '立新事', newTaskDialog: '立抄卡之事', startTaskOptions: '始其事', runningTasks: '行中', history: '往事', authorization: '所許之目', settings: '設置', about: '關於',
    source: '所出之目', destination: '所至之目', chooseSource: '擇並許所出', chooseDestination: '擇並許所至', refreshAuth: '刷新所許', noAuthorizedFolders: '尚無所許共享之目', prepareTask: '預備新事', choosePathsHint: '請擇所出與所至之目', sourceStorageCard: '所出之目／儲卡', destinationCard: '抄錄所至', notSelected: '尚未擇定', fileSize: '檔案大小', fileCount: '檔案數', createdAt: '立於', finishedAt: '終於', elapsed: '累時', taskProgress: '事之進度', waitingStart: '候始', copyPhase: '抄錄', verifyPhase: '驗檔', readyHint: '已備。擇目即可始。', copySpeed: '抄錄速', verifySpeed: '驗檔速', copyByteRate: '檔案抄錄量速', verifyByteRate: '檔案驗證量速', itemRateTitle: '項目速', stackedWaveform: '波形並列', verticalWaveform: '波形縱列', completedLabel: '已成', appearanceDescription: '擇明暗之貌與主色。', colorModeAndAccent: '色式與主色', colorMode: '色式', themeColor: '主色', systemColor: '系統色', applicationInfo: '應用之訊', update: '更新', aboutDescription: '觀版本並訪 ClipPort 之庫。', updateDescription: '自 GitHub Releases 取最新版。', githubOpen: '於 GitHub 開之', notificationChannels: '告知之道', priorityHint: '事逐一候行，勾先行者列於隊首。', waitingSetup: '候設定', generalDescription: '設介面語與誌、報告之預設位置。', languageAndFiles: '語與檔', notificationDescription: '事終以一道或數道外部管道告知。', sendScenarios: '告知之境', folderSelectedSyncFailed: '目已擇，所許之狀尚待同步。', retryAuthorizationSync: '復同步', folderAuthorizationReady: '所許已同步。', nativePickerFailed: '原生擇目失敗', enableCopy: '開', verifyFiles: '開', copyFilesAccessible: '抄錄檔案', verifyFilesAccessible: '驗其完整', priorityAccessible: '先行', subfolder: '所至子目之名', subfolderHint: '空則不立子目。', duplicate: '重檔之處', duplicateHint: '詢則先行餘檔，後逐一處重檔。', fileVerification: '檔案驗證', priority: '先行之事',
    operationFailed: '行之未成', signedInAs: '掌理者', system: '從系統', light: '明', dark: '暗', on: '開', off: '閉', addAuthorization: '增所許', revokeAuthorization: '撤所許', locate: '於檔案司中示之', authorizationHint: '惟列 fnOS 已許 ClipPort 且掌理者可至之目。',
    activeTasksEmpty: '今無行中之事', historyEmpty: '尚無往事', settingsSaved: '設置已存', notification: '告知', checkUpdate: '察更新', updateAvailable: '有 fnOS FPK 新版', noUpdate: '今已最新', updateGuide: '取其 x86 FPK，於 fnOS 應用中心升之。', returnTasks: '返事', permissionUnavailable: '權狀暫不可得', semanticPathFallback: '示原徑',
  },
};

const sharedResourceKeys: Partial<Record<keyof typeof zh, string>> = {
  pause: 'Button.Pause', resume: 'Button.Resume', restart: 'Button.Restart', verifyAgain: 'Button.Reverify',
  report: 'Button.ExportReport', remove: 'Button.DeleteRecord', selectAll: 'Button.SelectAll', batchDelete: 'Button.BatchDelete', batchReports: 'Button.BatchCreateReports',
  priority: 'Common.Priority', retry: 'Button.RetrySelected', overwrite: 'DuplicateAction.Overwrite', skip: 'DuplicateAction.Skip', createCopy: 'DuplicateAction.Copy',
  settings: 'Settings.Title', appearance: 'Settings.Appearance', language: 'Settings.Language', notification: 'Settings.Notifications',
};

export type TranslationKey = keyof typeof zh;

export function translator(language: Language) {
  return (key: TranslationKey): string => {
    const platform = platformOverrides[language][key];
    if (platform) return platform;
    const resourceKey = sharedResourceKeys[key];
    if (resourceKey) {
      const localized = (windowsResources[language] as Record<string, string>)[resourceKey];
      if (localized) return localized;
    }
    return zh[key];
  };
}
