namespace obxodka.Controls.Dialogs;

//закрытая иерархия тонов диалога с проверкой полноты перебора в компиляторе c# 15
public closed record DialogTone
{
    public static DialogTone Update { get; } = new DialogUpdateTone();
    public static DialogTone Delete { get; } = new DialogDeleteTone();
    public static DialogTone Error { get; } = new DialogErrorTone();
    public static DialogTone Warning { get; } = new DialogWarningTone();
    public static DialogTone Success { get; } = new DialogSuccessTone();
    public static DialogTone Info { get; } = new DialogInfoTone();
}

public sealed record DialogUpdateTone : DialogTone;

public sealed record DialogDeleteTone : DialogTone;

public sealed record DialogErrorTone : DialogTone;

public sealed record DialogWarningTone : DialogTone;

public sealed record DialogSuccessTone : DialogTone;

public sealed record DialogInfoTone : DialogTone;
