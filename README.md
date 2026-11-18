# Launcher

Свой Raycast для Windows 11 (C#/WPF, .NET 10): Alt+Q — приложения, файлы, пути, поиск в интернете,
музыка и команды плеера [Noir](https://github.com/trualontuu/player) («пауза», «дальше», «кс2»).

Часть кода общая с [Island](https://github.com/trualontuu/island) — репозитории должны лежать рядом:

```
spspsps/
  island/
  launcher/   ← этот репозиторий
  player/
```

```
dotnet publish -c Release -o dist
```

Проверка без окна: `Launcher.exe --snap <папка> "запрос1" "запрос2"`.
