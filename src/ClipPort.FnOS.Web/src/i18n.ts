import { windowsResources } from './generatedTranslations';
import type { Language } from './types';

const zh = {
  appSubtitle: 'NAS 文件复制与完整性校验',
  newTask: '新建任务', runningTasks: '运行任务', history: '历史任务', authorization: '授权目录', settings: '设置', about: '关于与更新',
  source: '源目录', destination: '目标目录', chooseSource: '选择并授权源目录', chooseDestination: '选择并授权目标目录',
  refreshAuth: '刷新授权目录', noAuthorizedFolders: '尚未授权任何共享目录', folderSelectedSyncFailed: '目录已选择，但授权列表同步失败。', nativePickerFailed: '原生目录选择失败',
  mode: '任务模式', copyAndVerify: '复制并校验', copyOnly: '仅复制', verifyOnly: '仅校验', enableCopy: '复制文件', verifyFiles: '校验文件',
  subfolder: '目标子目录（可选）', duplicate: '重复文件', ask: '逐项询问', overwrite: '覆盖', skip: '跳过', createCopy: '创建副本',
  algorithm: '校验算法', verifyTiming: '校验时机', afterCopy: '复制后校验', opportunisticDuringCopy: '拷贝时校验（可用时）', priority: '优先任务',
  create: '创建任务', tasks: '任务与历史', emptyTasks: '还没有任务', pause: '暂停', resume: '恢复', cancel: '取消', restart: '重新开始', verifyAgain: '再次校验',
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
} as const;

const platformOverrides: Record<Language, Partial<Record<keyof typeof zh, string>>> = {
  'zh-CN': {},
  'en-US': {
    appSubtitle: 'NAS file copy and integrity verification', newTask: 'New task', runningTasks: 'Running', history: 'History', authorization: 'Authorized folders', settings: 'Settings', about: 'About & updates',
    source: 'Source folder', destination: 'Destination folder', chooseSource: 'Select and authorize source', chooseDestination: 'Select and authorize destination', refreshAuth: 'Refresh authorized folders', noAuthorizedFolders: 'No shared folder has been authorized',
    folderSelectedSyncFailed: 'The folder was selected, but the authorization list could not be refreshed.', nativePickerFailed: 'Native folder selection failed', enableCopy: 'Copy files', verifyFiles: 'Verify files', subfolder: 'Destination subfolder (optional)',
    priority: 'Priority task', tasks: 'Tasks and history', emptyTasks: 'No tasks yet', browserAuthHint: 'A fnOS authorization window will open; refresh folders after returning', signedInAs: 'Administrator',
    system: 'Use system', light: 'Light', dark: 'Dark', operationFailed: 'Operation failed', authorizationHint: 'Only folders granted to ClipPort and accessible to the current administrator are shown.', addAuthorization: 'Add authorization', revokeAuthorization: 'Revoke', locate: 'Show in File Manager',
    readable: 'Readable', writable: 'Writable', notWritable: 'Read only', activeTasksEmpty: 'No active tasks', historyEmpty: 'No task history', copyWaveform: 'Copy throughput', verifyWaveform: 'Verification throughput', byteRate: 'Byte rate', itemRate: 'File rate', noWaveform: 'No waveform samples',
    appearance: 'Appearance', accent: 'Accent', general: 'General', saveSettings: 'Save settings', settingsSaved: 'Settings saved', reportExportDirectory: 'Default report export folder', chooseReportDirectory: 'Choose report folder', notification: 'Notifications', notifyCompleted: 'Notify when a task completes', notifyFailed: 'Notify when a task fails',
    addChannel: 'Add channel', channelName: 'Name', channelKind: 'Type', enabled: 'Enabled', endpoint: 'Webhook / Bark URL', smtpHost: 'SMTP host', smtpPort: 'Port', smtpUsername: 'Username', smtpPassword: 'Password', smtpFrom: 'Sender', smtpRecipients: 'Recipients', savedSecret: 'Saved securely; leave blank to keep it', testSend: 'Send test', testSucceeded: 'Test sent', deleteChannel: 'Delete channel',
    version: 'Version', repository: 'Repository', checkUpdate: 'Check for updates', latestVersion: 'Latest version', updateAvailable: 'A fnOS FPK update is available', noUpdate: 'You are up to date', downloadFpk: 'Download FPK', openAppCenter: 'Open app settings', updateGuide: 'Download the matching x86 FPK, then finish the upgrade in fnOS App Center.', exportSucceeded: 'Reports exported',
  },
  lzh: {
    appSubtitle: 'NAS 檔案徙置與驗真', newTask: '立新事', runningTasks: '行中', history: '往事', authorization: '所許之目', settings: '設置', about: '關於與更新',
    source: '所出之目', destination: '所至之目', chooseSource: '擇並許所出', chooseDestination: '擇並許所至', refreshAuth: '刷新所許', noAuthorizedFolders: '尚無所許共享之目', folderSelectedSyncFailed: '目已擇，而所許之錄未能同步。', nativePickerFailed: '原生擇目失敗', enableCopy: '抄錄檔案', verifyFiles: '驗其完整', subfolder: '所至子目（可無）', priority: '先行之事',
    operationFailed: '行之未成', signedInAs: '掌理者', system: '從系統', light: '明', dark: '暗', addAuthorization: '增所許', revokeAuthorization: '撤所許', locate: '於檔案司中示之', authorizationHint: '惟列 fnOS 已許 ClipPort 且掌理者可至之目。',
    activeTasksEmpty: '今無行中之事', historyEmpty: '尚無往事', settingsSaved: '設置已存', notification: '告知', checkUpdate: '察更新', updateAvailable: '有 fnOS FPK 新版', noUpdate: '今已最新', updateGuide: '取其 x86 FPK，於 fnOS 應用中心升之。',
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
