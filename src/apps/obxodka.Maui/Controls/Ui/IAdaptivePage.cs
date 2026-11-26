namespace obxodka.Controls.Ui;

//единый контракт получения снимка адаптивной геометрии окна всеми экранами приложения
public interface IAdaptivePage
{
    void ApplyViewport(AdaptiveViewport viewport);
}
