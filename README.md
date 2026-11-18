<div align="center">

# Launcher

**Свой Raycast для Windows 11**

Одна строка вместо меню «Пуск»: приложения, файлы, музыка и команды — по <kbd>Alt</kbd> + <kbd>Q</kbd>.

![C#](https://img.shields.io/badge/C%23-111?style=flat-square&logo=dotnet&logoColor=white)
![.NET 10](https://img.shields.io/badge/.NET-10-111?style=flat-square&logo=dotnet&logoColor=white)
![WPF](https://img.shields.io/badge/WPF-111?style=flat-square&logo=windows11&logoColor=white)
![Windows 11](https://img.shields.io/badge/Windows-11-111?style=flat-square&logo=windows11&logoColor=white)

<img src=".github/assets/launcher.png" alt="Launcher: поиск и команды плеера" width="900">

</div>

## Что умеет

- **Собственный индекс** — приложения из меню «Пуск» и десятки тысяч файлов в памяти, без Windows Search и Everything. Результаты появляются по мере ввода.
- **Музыка из строки** — ищет треки в Яндекс Музыке и запускает их в [Noir](https://github.com/trualontuu/player); команды «пауза», «дальше», «лайк», «кс2».
- **Учится на тебе** — запоминает, что ты чаще открываешь, и понимает сокращения: «тг» → Telegram.
- **Быстрые клавиши** — <kbd>Alt</kbd>+<kbd>1…9</kbd> открыть результат, <kbd>Ctrl</kbd>+<kbd>Enter</kbd> показать в папке, <kbd>Ctrl</kbd>+<kbd>C</kbd> скопировать путь.
- **Лёгкий** — ~60 МБ памяти, 0% процессора в простое.

## Сборка

Часть кода общая с [Island](https://github.com/trualontuu/island) — репозитории должны лежать рядом:

```
projects/
  island/
  launcher/   ← этот репозиторий
  player/
```

```
dotnet publish -c Release -o dist
```

Проверка без окна: `Launcher.exe --snap <папка> "запрос1" "запрос2"` — снимки выдачи и замеры в `report.txt`.

Настройки — `%APPDATA%\Launcher\settings.json` (горячая клавиша, папки для индекса, исключения, сокращения).
