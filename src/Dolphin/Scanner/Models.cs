namespace Dolphin.Scanner;

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
