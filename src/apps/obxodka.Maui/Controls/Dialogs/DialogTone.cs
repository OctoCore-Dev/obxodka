namespace obxodka.Controls.Dialogs;

//закрытая иерархия тонов диалога с проверкой полноты перебора в компиляторе c# 15
public closed class DialogTone
{
    public static DialogTone Update { get; } = new DialogUpdateTone();
    public static DialogTone Delete { get; } = new DialogDeleteTone();
    public static DialogTone Error { get; } = new DialogErrorTone();
    public static DialogTone Warning { get; } = new DialogWarningTone();
    public static DialogTone Success { get; } = new DialogSuccessTone();
    public static DialogTone Info { get; } = new DialogInfoTone();
}

public sealed class DialogUpdateTone : DialogTone;

public sealed class DialogDeleteTone : DialogTone;

public sealed class DialogErrorTone : DialogTone;

public sealed class DialogWarningTone : DialogTone;

public sealed class DialogSuccessTone : DialogTone;

public sealed class DialogInfoTone : DialogTone;
