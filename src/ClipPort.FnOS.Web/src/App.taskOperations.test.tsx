import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from './App';
import type { ClipPortTask } from './types';

const api = vi.hoisted(() => ({
  loadSession: vi.fn(), loadFolders: vi.fn(), loadTasks: vi.fn(), loadSettings: vi.fn(),
  taskAction: vi.fn(), deleteTasks: vi.fn(), exportReports: vi.fn(),
  submitDuplicates: vi.fn(), submitFailures: vi.fn(),
}));
const sdk = vi.hoisted(() => ({
  isStandaloneWeb: false, pickFolder: vi.fn(), getPlatformConfig: vi.fn(), listen: vi.fn(),
  parseCallback: vi.fn(), setTitle: vi.fn(), openFileManager: vi.fn(), openApplicationSettings: vi.fn(), openUrl: vi.fn(),
}));
vi.mock('./api', async importOriginal => ({ ...await importOriginal<typeof import('./api')>(), ...api }));
vi.mock('./fnosSdk', () => ({ fnosSdk: sdk }));

class SilentWebSocket { onmessage: ((event: MessageEvent) => void) | null = null; onclose: (() => void) | null = null; close() {} }

const task = (id: string, status: ClipPortTask['status'], patch: Partial<ClipPortTask> = {}): ClipPortTask => ({
  id, status, displayName: id, createdAt: '2026-08-30T00:00:00Z',
  request: { mode: 'copyAndVerify', sourcePath: '/vol1/source', destinationPath: '/vol1/target', existingFilePolicy: 'ask', verificationAlgorithm: 'sha256', verificationExecutionMode: 'afterCopy', isPriority: false },
  duplicateFiles: [], failedFiles: [], errors: [], warnings: [], reportFileName: `${id}.txt`,
  copyByteSpeedSamples: [10, 20], copyItemSpeedSamples: [1, 2], copyThroughputProgressSamples: [0, 1],
  verifyByteSpeedSamples: [8, 12], verifyItemSpeedSamples: [1, 1], verifyThroughputProgressSamples: [0, 1],
  ...patch,
});

describe('task operations, selections and decisions', () => {
  beforeEach(() => {
    vi.clearAllMocks(); vi.stubGlobal('WebSocket', SilentWebSocket);
    api.loadSession.mockResolvedValue({ isAdmin: true, userId: 1000, username: 'admin', csrfToken: 'csrf', language: 'zh-CN', systemVersion: '1.2.0401', isCompatible: true });
    api.loadFolders.mockResolvedValue([]);
    api.loadSettings.mockResolvedValue({ version: 1, theme: 'system', accent: 'system', language: 'simplifiedChinese', reportExportDirectory: '/vol1/reports', notifyOnTaskCompleted: true, notifyOnTaskFailed: true, channels: [] });
    api.taskAction.mockResolvedValue(undefined); api.deleteTasks.mockResolvedValue(undefined);
    api.exportReports.mockResolvedValue({ exportedCount: 1, files: ['/vol1/reports/done.txt'] });
    api.submitDuplicates.mockResolvedValue(undefined); api.submitFailures.mockResolvedValue(undefined);
    sdk.getPlatformConfig.mockResolvedValue({ language: 'zh-CN', theme: 'light' }); sdk.listen.mockResolvedValue(undefined); sdk.setTitle.mockResolvedValue(undefined);
  });

  it('runs active actions, renders persisted waveforms and handles selected history in bulk', async () => {
    api.loadTasks.mockResolvedValue([task('running-job', 'running'), task('paused-job', 'paused'), task('done-job', 'completed')]);
    render(<App />); await screen.findByText('管理员 · admin');

    fireEvent.click(screen.getByRole('button', { name: '运行任务' }));
    fireEvent.click(screen.getByRole('button', { name: '暂停' }));
    fireEvent.click(screen.getByRole('button', { name: '继续' }));
    await waitFor(() => {
      expect(api.taskAction).toHaveBeenCalledWith('running-job', 'pause');
      expect(api.taskAction).toHaveBeenCalledWith('paused-job', 'resume');
    });

    fireEvent.click(screen.getByRole('button', { name: '历史任务' }));
    fireEvent.click(screen.getByRole('checkbox', { name: '已选择' }));
    fireEvent.click(screen.getByRole('button', { name: '批量创建报告' }));
    await waitFor(() => expect(api.exportReports).toHaveBeenCalledWith(['done-job'], '/vol1/reports'));

    fireEvent.click(screen.getByRole('button', { name: '详情' }));
    const dialog = await screen.findByRole('dialog', { name: 'done-job' });
    expect(within(dialog).getByRole('img', { name: '复制吞吐' })).toBeInTheDocument();
    expect(within(dialog).getByRole('img', { name: '校验吞吐' })).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole('button', { name: 'close' }));

    fireEvent.click(screen.getByRole('button', { name: '重新开始' }));
    fireEvent.click(screen.getByRole('button', { name: '重新校验' }));
    await waitFor(() => {
      expect(api.taskAction).toHaveBeenCalledWith('done-job', 'restart');
      expect(api.taskAction).toHaveBeenCalledWith('done-job', 'verify');
    });
    fireEvent.click(screen.getByRole('button', { name: '批量删除' }));
    await waitFor(() => expect(api.deleteTasks).toHaveBeenCalledWith(['done-job']));
  });

  it('submits batch and per-item duplicate choices', async () => {
    api.loadTasks.mockResolvedValue([task('duplicate-job', 'awaitingDuplicateDecision', {
      duplicateFiles: [{ relativePath: 'a.txt' }, { relativePath: 'b.txt' }], reportFileName: null,
    })]);
    render(<App />); await screen.findByText('管理员 · admin');
    const dialog = await screen.findByRole('dialog', { name: '需要处理重复文件' });
    fireEvent.click(within(dialog).getByRole('button', { name: '全部覆盖' }));
    fireEvent.change(within(dialog).getAllByRole('combobox')[1], { target: { value: 'skip' } });
    fireEvent.click(within(dialog).getByRole('button', { name: '提交决定' }));
    await waitFor(() => expect(api.submitDuplicates).toHaveBeenCalledWith('duplicate-job', [
      { relativePath: 'a.txt', decision: 'overwrite' }, { relativePath: 'b.txt', decision: 'skip' },
    ]));
  });

  it('submits selected failed items for retry', async () => {
    api.loadTasks.mockResolvedValue([task('failure-job', 'awaitingFailureDecision', {
      failedFiles: [{ relativePath: 'broken.txt', error: 'failed', isVerificationMismatch: false }], reportFileName: null,
    })]);
    render(<App />); await screen.findByText('管理员 · admin');
    const dialog = await screen.findByRole('dialog', { name: '需要处理失败项' });
    await waitFor(() => expect(within(dialog).getByRole('checkbox')).toBeChecked());
    fireEvent.click(within(dialog).getByRole('button', { name: '重试所选' }));
    await waitFor(() => expect(api.submitFailures).toHaveBeenCalledWith('failure-job', 'retry', ['broken.txt']));
  });
});
