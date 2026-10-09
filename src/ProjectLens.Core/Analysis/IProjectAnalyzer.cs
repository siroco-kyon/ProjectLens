using ProjectLens.Core.Models;

namespace ProjectLens.Core.Analysis;

public interface IProjectAnalyzer
{
    Task<AnalysisResult> AnalyzeAsync(AnalysisOptions options, IProgress<AnalysisProgress>? progress = null, CancellationToken cancellationToken = default);
}
