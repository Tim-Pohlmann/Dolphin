namespace Dolphin.Scanner;

internal record ValidationDiagnostic(
    ValidationRange Range,
    int Severity,
    string Source,
    string Message,
    bool Pending);

internal record ValidationRange(ValidationPosition Start, ValidationPosition End);

internal record ValidationPosition(int Line, int Character);
