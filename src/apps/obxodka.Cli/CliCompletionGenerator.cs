namespace obxodka.Cli;

//генератор скриптов автодополнения для powershell, bash и zsh
public static class CliCompletionGenerator
{
    public static readonly string[] TopLevelCommands =
    [
        "login", "auth", "logout", "whoami", "servers", "connect", "start",
        "disconnect", "stop", "status", "config", "split", "diag", "completion", "version", "help"
    ];

    //выдача скрипта автодополнения по имени оболочки
    public static string? Generate(string shell) => shell.ToLowerInvariant() switch
    {
        "powershell" or "pwsh" => GeneratePowerShell(),
        "bash" => GenerateBash(),
        "zsh" => GenerateZsh(),
        _ => null
    };

    private static string GeneratePowerShell() =>
        """
        Register-ArgumentCompleter -Native -CommandName @('obxodka', 'obx') -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)
            $commands = @('login', 'auth', 'logout', 'whoami', 'servers', 'connect', 'start', 'disconnect', 'stop', 'status', 'config', 'split', 'diag', 'completion', 'version', 'help')
            $elements = $commandAst.CommandElements
            if ($elements.Count -le 2) {
                $commands | Where-Object { $_ -like "$wordToComplete*" } | ForEach-Object {
                    [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
                }
                return
            }
            $sub = $elements[1].Extent.Text.ToLowerInvariant()
            $candidates = switch ($sub) {
                'connect'    { @('--kill-switch', '--no-kill-switch', '--adblock', '--no-adblock', '--block-ipv6', '--mtu', '--json') }
                'start'      { @('--kill-switch', '--no-kill-switch', '--adblock', '--no-adblock', '--block-ipv6', '--mtu', '--json') }
                'servers'    { @('--ping', '--json') }
                'status'     { @('--watch', '--json') }
                'config'     { @('get', 'set', 'kill-switch', 'adblock', 'block-ipv6', 'auto-reconnect', 'force-websocket', 'bypass-ru', 'sync-timezone') }
                'split'      { @('list', 'add', 'remove') }
                'completion' { @('powershell', 'bash', 'zsh') }
                'diag'       { @('--json') }
                default      { @('--help', '--json') }
            }
            $candidates | Where-Object { $_ -like "$wordToComplete*" } | ForEach-Object {
                [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
            }
        }
        """;

    private static string GenerateBash() =>
        """
        _obxodka_completions() {
            local cur="${COMP_WORDS[COMP_CWORD]}"
            local cmd="${COMP_WORDS[1]}"
            if [[ ${COMP_CWORD} -eq 1 ]]; then
                COMPREPLY=( $(compgen -W "login auth logout whoami servers connect start disconnect stop status config split diag completion version help" -- "${cur}") )
                return 0
            fi
            case "${cmd}" in
                connect|start) COMPREPLY=( $(compgen -W "--kill-switch --no-kill-switch --adblock --no-adblock --block-ipv6 --mtu --json" -- "${cur}") ) ;;
                servers)       COMPREPLY=( $(compgen -W "--ping --json" -- "${cur}") ) ;;
                status)        COMPREPLY=( $(compgen -W "--watch --json" -- "${cur}") ) ;;
                config)        COMPREPLY=( $(compgen -W "get set kill-switch adblock block-ipv6 auto-reconnect force-websocket bypass-ru sync-timezone" -- "${cur}") ) ;;
                split)         COMPREPLY=( $(compgen -W "list add remove" -- "${cur}") ) ;;
                completion)    COMPREPLY=( $(compgen -W "powershell bash zsh" -- "${cur}") ) ;;
                diag)          COMPREPLY=( $(compgen -W "--json" -- "${cur}") ) ;;
            esac
        }
        complete -F _obxodka_completions obxodka obx
        """;

    private static string GenerateZsh() =>
        """
        #compdef obxodka obx
        _obxodka() {
            local -a commands
            commands=(
                'login:войти в аккаунт по email'
                'auth:алиас для login'
                'logout:выйти из аккаунта'
                'whoami:показать текущий аккаунт и подписку'
                'servers:список доступных серверов'
                'connect:подключиться к VPN'
                'start:алиас для connect'
                'disconnect:отключиться от VPN'
                'stop:алиас для disconnect'
                'status:состояние туннеля и статистика'
                'config:чтение и изменение настроек'
                'split:управление раздельным туннелированием'
                'diag:диагностика сети, DNS и TLS'
                'completion:генерация автодополнения оболочки'
            )
            _describe 'command' commands
        }
        compdef _obxodka obxodka obx
        """;
}
