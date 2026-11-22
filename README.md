# Umamusume companion — мод для Jalopy

> [!IMPORTANT]
> ## Мод для Jalopy полностью сделан вайбкодингом
> Я описывал, как должна вести себя спутница, а код мода писал ИИ.

## Установка

1. Если вы впервые ставите моды на Jalopy, [установите JaLoader](https://github.com/theLeaxx/JaLoader/wiki/Installing-JaLoader-via-JaPatcher), запустите игру один раз и закройте её.
2. [Скачайте установщик для Windows](https://github.com/MuhaAbadula/Umamusume-companion/releases/download/v0.55.1/Umamusume-companion-0.55.1-Windows.exe), запустите его и нажмите **ОК**. Он сам найдёт папку игры. Если попросит указать файл, выберите `Jalopy.exe`.

Запустите Jalopy, выберите спутницу в настройках мода и перезапустите игру. Чтобы дать команду, удерживайте **V**, наведите мышь на действие и отпустите клавишу.

<details>
<summary>Ручная установка и обновление старых версий</summary>

Если спутница и команды раньше стояли двумя отдельными модами, закройте игру и перенесите их старые DLL из папки модов в резервную папку перед установкой.

Для ручной установки распакуйте [ZIP версии 0.55.1](https://github.com/MuhaAbadula/Umamusume-companion/releases/download/v0.55.1/Umamusume-companion-0.55.1.zip) рядом с `Jalopy.exe`. Если вы выбрали другую папку модов в JaLoader, перенесите туда содержимое `Jalopy_Data/Mods` из архива. Перед ручным обновлением сохраните копию своих изменённых файлов.

</details>

## Доступные спутницы

- Агнес Тахион (Agnes Tachyon)
- Матиканэ Фукукитару (Matikanefukukitaru)
- Нарита Тайшин (Narita Taishin)
- Найс Нейча (Nice Nature)
- Свип Тошо (Sweep Tosho)

## Сборка из исходников

Установите .NET SDK, Jalopy и JaLoader. В корне проекта выполните команду,
подставив путь к игре.

```powershell
dotnet build UmamusumeCompanion.csproj -c Release "-p:GameDir=D:\SteamLibrary\steamapps\common\Jalopy"
```

Сборка создаёт `bin/Release/net35/UmamusumeCompanion.dll`. Скопируйте DLL в
`Jalopy_Data/Mods`. Для запуска из исходников понадобятся ресурсы спутницы
из установочного архива или прежней установки мода. Тексты из `models/`
накладываются на одноимённые папки моделей, а файлы из `Paintjobs/` идут
в папку `Paintjobs` рядом с исполняемым файлом игры.

Состав пакета, портрет Nice Nature и заметки по её сценам собраны в [справочнике спутниц](docs/companions.md).

## Структура

- `src/Core` содержит точку входа JaLoader и регистрацию настроек.
- `src/Commands` отвечает за круговое меню и доступность команд.
- `src/Travel` содержит выход из машины, погрузку, ночлег и возвращение к машине.
- `src/Characters` отвечает за модели, анимации, голос и поведение спутниц.
- `src/Paint`, `src/Radio`, `src/Luck`, `src/Pranks` и `src/Experiments`
  содержат соответствующие игровые механики.
- `src/Integration` содержит патчи игры, а `src/Diagnostics` — панель отладки.

## Правообладатель

Модели, персонажи и их голоса принадлежат компании Cygames.
