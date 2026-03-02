namespace Dolphin.Semgrep;

public enum Severity { Error, Warning, Info }

public record Finding(
    string RuleId,
    Severity Severity,
    string FilePath,
    int Line,
    int Column,
    string Message,
    string MatchedText
);

// Semgrep JSON output shapes
public record SemgrepOutput(
    List<SemgrepResult> Results,
    SemgrepErrors Errors
);

public record SemgrepResult(
    string CheckId,
    SemgrepPath Path,
    SemgrepStart Start,
    SemgrepExtra Extra
);

public record SemgrepPath(string Value);

public record SemgrepStart(int Line, int Col);

public record SemgrepExtra(
    string Message,
    string Severity,
    string Lines
);

public record SemgrepErrors(List<string> Items);
