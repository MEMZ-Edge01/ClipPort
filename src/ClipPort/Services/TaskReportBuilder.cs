using System.Text;
using ClipPort.Models;

namespace ClipPort.Services;

public static class TaskReportBuilder
{
    public static string Build(CopyResult result, JobHistoryItem job)
    {
        var report = CreateHeader("Report.Title", job);
        report.AppendLine(ResourceService.Format(
            "Report.FileCount",
            result.FileCount.ToString("N0")));
        report.AppendLine(ResourceService.Format(
            "Report.DataSize",
            DisplayFormatting.FormatBytes(result.TotalBytes)));
        report.AppendLine(job.CopyEnabled
            ? ResourceService.Format(
                "Report.CopyDuration",
                DisplayFormatting.FormatDuration(result.CopyDuration))
            : ResourceService.GetString("Report.CopyDisabled"));
        report.AppendLine(ResourceService.Format(
            "Report.VerifyDuration",
            DisplayFormatting.FormatDuration(result.VerifyDuration)));
        if (job.CopyEnabled)
        {
            report.AppendLine(ResourceService.Format(
                "Report.CopyAlgorithm",
                ResourceService.GetString(job.UseFastCopyAlgorithm
                    ? "Report.ManagedPipeline"
                    : "Report.SequentialCopy")));
            report.AppendLine(ResourceService.Format(
                "Report.CopiedProgress",
                result.CopiedFiles.ToString("N0"),
                result.FileCount.ToString("N0"),
                DisplayFormatting.FormatBytes(result.CopiedBytes)));
        }
        report.AppendLine(ResourceService.Format(
            "Report.VerificationAlgorithm",
            result.VerificationPerformed
                ? "SHA-256"
                : ResourceService.GetString("Common.Disabled")));
        if (result.VerificationPerformed)
        {
            report.AppendLine(ResourceService.Format(
                "Report.VerifiedProgress",
                result.VerifiedFileCount.ToString("N0"),
                result.FileCount.ToString("N0")));
        }
        report.AppendLine(ResourceService.Format(
            "Report.FinalResult",
            ResourceService.GetString(
                result.Success ? "Common.Passed" : "Common.Failed")));

        if (result.VerifiedFiles.Count > 0)
        {
            report.AppendLine();
            report.AppendLine(ResourceService.GetString("Report.VerificationDetails"));
            foreach (FileVerificationResult file in result.VerifiedFiles)
            {
                report.AppendLine(file.IsMatch
                    ? ResourceService.Format(
                        "Report.PassedVerificationEntry",
                        file.RelativePath,
                        DisplayFormatting.FormatBytes(file.Length),
                        file.SourceSha256)
                    : ResourceService.Format(
                        "Report.FailedVerificationEntry",
                        file.RelativePath,
                        file.SourceSha256,
                        file.DestinationSha256,
                        file.Error ?? ResourceService.GetString("Common.Failed")));
            }
        }

        AppendFailures(report, result.FailedFiles);
        AppendWarnings(report, result.Warnings);
        AppendDuplicates(report, job);
        return report.ToString();
    }

    public static string BuildIncomplete(JobHistoryItem job)
    {
        var report = CreateHeader("Report.IncompleteTitle", job);
        report.AppendLine(ResourceService.Format(
            "Report.TaskStatus",
            ResourceService.GetString(job.StatusText)));
        if (job.CopyEnabled)
        {
            report.AppendLine(ResourceService.Format(
                "Report.CopyAlgorithm",
                ResourceService.GetString(job.UseFastCopyAlgorithm
                    ? "Report.ManagedPipeline"
                    : "Report.SequentialCopy")));
            report.AppendLine(ResourceService.Format(
                "Report.CopiedProgress",
                job.CopiedFiles.ToString("N0"),
                job.FileCount.ToString("N0"),
                DisplayFormatting.FormatBytes(job.CopiedBytes)));
        }
        else
        {
            report.AppendLine(ResourceService.GetString("Report.CopyDisabled"));
        }

        report.AppendLine(job.VerificationEnabled
            ? ResourceService.Format(
                "Report.VerifiedProgress",
                job.VerifiedFiles.ToString("N0"),
                job.FileCount.ToString("N0"))
            : ResourceService.GetString("Report.VerificationDisabled"));
        if (!string.IsNullOrWhiteSpace(job.ErrorMessage))
        {
            report.AppendLine(ResourceService.Format(
                "Report.Details",
                ResourceService.GetString(job.ErrorMessage)));
        }

        AppendFailures(report, job.FailedFiles);
        AppendDuplicates(report, job);
        return report.ToString();
    }

    private static StringBuilder CreateHeader(string titleKey, JobHistoryItem job)
    {
        var report = new StringBuilder();
        report.AppendLine(ResourceService.GetString(titleKey));
        report.AppendLine(new string('=', 42));
        report.AppendLine(ResourceService.Format("Report.TaskName", job.DisplayName));
        report.AppendLine(ResourceService.Format("Report.SourceDirectory", job.SourcePath));
        report.AppendLine(ResourceService.Format("Report.DestinationDirectory", job.DestinationPath));
        report.AppendLine(ResourceService.Format(
            "Report.StartTime",
            job.StartedAt.ToString("yyyy-MM-dd HH:mm:ss")));
        report.AppendLine(ResourceService.Format(
            "Report.EndTime",
            job.FinishedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "--"));
        return report;
    }

    private static void AppendFailures(
        StringBuilder report,
        IReadOnlyList<FileOperationFailure> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        report.AppendLine();
        report.AppendLine(ResourceService.Format(
            "Report.FailedFiles",
            failures.Count.ToString("N0")));
        foreach (FileOperationFailure failure in failures)
        {
            report.AppendLine(ResourceService.Format(
                "Report.FailureEntry",
                failure.RelativePath,
                ResourceService.GetString(failure.StageText),
                failure.Error));
        }
    }

    private static void AppendWarnings(
        StringBuilder report,
        IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        report.AppendLine();
        report.AppendLine(ResourceService.Format(
            "Report.Warnings",
            warnings.Count.ToString("N0")));
        foreach (string warning in warnings)
        {
            report.AppendLine(ResourceService.Format(
                "Report.WarningEntry",
                warning));
        }
    }

    private static void AppendDuplicates(StringBuilder report, JobHistoryItem job)
    {
        if (job.DuplicateFiles.Count == 0)
        {
            return;
        }

        report.AppendLine();
        report.AppendLine(ResourceService.Format(
            "Report.DuplicateHandling",
            job.DuplicateFiles.Count.ToString("N0")));
        foreach (DuplicateFileConflict conflict in job.DuplicateFiles)
        {
            ExistingFilePolicy decision = job.DuplicateDecisions.TryGetValue(
                conflict.RelativePath,
                out ExistingFilePolicy selected)
                ? selected
                : ExistingFilePolicy.Ask;
            report.AppendLine(ResourceService.Format(
                "Report.DuplicateEntry",
                ResourceService.GetString(GetDuplicatePolicyKey(decision)),
                conflict.RelativePath));
            report.AppendLine(ResourceService.Format(
                "Report.SourceEntry",
                conflict.SourcePath));
            report.AppendLine(ResourceService.Format(
                "Report.ConflictEntry",
                conflict.DestinationPath));
        }
    }

    private static string GetDuplicatePolicyKey(ExistingFilePolicy policy) => policy switch
    {
        ExistingFilePolicy.Overwrite => "Button.OverwriteSelected",
        ExistingFilePolicy.Skip => "Button.SkipSelected",
        ExistingFilePolicy.CreateCopy => "Button.CopySelected",
        _ => "Info.ChooseActionEach"
    };
}
