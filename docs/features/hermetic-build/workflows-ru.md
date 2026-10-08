# Герметичная сборка: сценарии работы

Короткий путеводитель для владельца продукта. Как команда живёт с lock-файлами: от первой сборки
у разработчика до CI, релиза и офлайна. Это пересказ [../../hermetic/workflows.md](../../hermetic/workflows.md),
сверенный с исходниками ветки `feature/hermetic-build` на 2026-10-04 (Xake 3.5).

Пометки:

- **(запускалось)**: пример или сообщение проверено для этой страницы (macOS, SDK 10.0.401 и
  8.0.425, временная папка).
- **(не запускалось)**: написано по исходникам, не проверено.

Версии. Опубликованы `Xake 3.5.0.23` и `Xake.Hermetic.Dotnet 0.1.0.22`. Вместе они грузятся и
работают (запускалось), хотя 0.1.0.22 собран на Xake 3.4. Но часть описанного ниже есть только в
ветке и выйдет в `Xake.Hermetic.Dotnet` 0.2 (пара к Xake 3.5): CI-флаг, пути с токенами в
сообщениях, `packageroot`/`norestore`, проверка sha512 уже лежащих пакетов. Такие места помечены
**[0.2]**. С 0.1.0.22 под CI отсутствующий lock просто записывается (запускалось).

## 1. Зачем

- **Lock** это JSON-файл в репозитории. В нём ровно то, что получает компилятор: командная
  строка, исходники, ссылки, анализаторы, сам компилятор. У каждого внешнего файла записан SHA-256.
- **Герметично** здесь значит: сборка читает только то, что записано в lock, и падает, если
  что-то отличается. Никаких «взял, что нашлось на машине».
- **Гарантирует**: одинаковый вход компилятора на всех машинах; явное обновление lock (никогда
  молча); падение CI без lock или с устаревшим lock; подмена файла ловится по хешу.
- **Не гарантирует**: одинаковые байты на выходе сами по себе (нужны `/deterministic` и тот же
  путь checkout); наличие SDK (его ставят сами, по `global.json`); работу тестов и публикации
  (восстанавливается только то, что читает компилятор).
- Lock не содержит путей машины: вместо них токены `$(ProjectRoot)`, `$(NuGetPackageRoot)`,
  `$(DotnetRoot)`.

## 2. Герметичный режим

**В работе: режим описан ([hermetic-mode.md](hermetic-mode.md)), реализация следующими PR.**
Ничего из этого раздела пока не запускалось.

Один флаг в скрипте, `HERMETIC=on` (по умолчанию выключен), и lock становится **полным**: всё, что
читает компилятор, либо лежит в репозитории, либо восстанавливается из папки пакетов, либо явно
объявлено как prerequisite и проверяется до сборки. Флаг не зависит от `CI`: `CI` про процесс
(lock не записывается, только сверяется), `HERMETIC` про содержимое (что lock вправе называть).

**Инвариант одной фразой:** запись lock не называет ни одного пути вне `$(ProjectRoot)` и
`$(NuGetPackageRoot)`, кроме путей, покрытых объявленным в записи prerequisite.

**Герметика + prerequisites.** Prerequisite это то, что окружение обязано дать само и что
проверяется заранее, с понятным сообщением. Сегодня такой один: .NET SDK точной версии. Пример:
у F# нет компилятора в NuGet, `fsc.dll` есть только в SDK. Поэтому для fsc SDK разрешён как
prerequisite, но только если `global.json` закрепляет его точно (`"rollForward": "disable"`), а
запись lock это фиксирует (`"Prerequisites": [{ "Kind": "dotnet-sdk", "Version": "10.0.401", ... }]`).
Всё остальное (ссылки, `FSharp.Core`, фасад `netstandard.dll`) идёт из пакетов. Для C# компромисс
не нужен: компилятор есть в пакете `Microsoft.Net.Compilers.Toolset`, значит под `HERMETIC=on`
обязателен `CSC_TOOLSET`.

Минимальный скрипт (не запускалось; формат списка `NETCORE_REF_VERSION` определит PR #37):

```fsharp
do xakeScript {
    var "HERMETIC" "on"                     // проверять полноту lock
    var "NUGET_PACKAGES" ".nuget/packages"  // своя папка пакетов: её кэшируют и переносят
    var "CSC_TOOLSET" "4.12.0"              // csc из NuGet, не из SDK
    var "NETCORE_REF_VERSION" "10.0.12"     // targeting pack net10.0 из NuGet, по версии на major.minor
    rules [
        "out/app.dll" ..> csc {
            src !!"src/*.cs"
            out (File.make "out/app.dll")
            lock "locks/app.json"
        }
    ]
}
```

Что будет, если что-то забыть: сборка падает при разрешении компиляции, до компилятора, и
называет путь и переменную, которая это чинит. Нарушения одной компиляции выводятся все сразу:

```
'app': HERMETIC=on: the compiler <dotnet>/sdk/10.0.401/Roslyn/bincore/csc.dll comes from the .NET SDK; set CSC_TOOLSET (or toolset in the csc block) to take csc from the Microsoft.Net.Compilers.Toolset package
'app': HERMETIC=on: the reference assemblies for net10.0 come from <dotnet>/packs/Microsoft.NETCore.App.Ref/10.0.12/ref/net10.0 under the .NET SDK; add a 10.0 version to NETCORE_REF_VERSION (this SDK bundles 10.0.12)
```

Lock, нарушающий инвариант, не записывается (`Lock.record`, `csc { lock }`, `Project.import`), а
старый lock с путями SDK под `HERMETIC=on` не компилируется: его надо перезаписать.

Чего режим не делает: не выключает restore (это `NUGET_FETCH=off`, отдельно), не трогает
сервер компилятора (`CSC_SERVER`), не меняет, откуда что берётся. Он только проверяет; чинят
переменные и `global.json`.

Зачем это продукту:

- **Воспроизводимость.** Lock полный: каждый вход либо восстанавливается из папки пакетов по
  точной версии и хешу, либо объявлен как prerequisite и проверен до сборки. Чистая машина
  получает ту же компиляцию; «на CI не нашёлся pack» превращается в ошибку на машине
  разработчика с именем переменной.
- **Скорость.** Нет оценки msbuild: компиляция собирается прямо из lock. Нет restore, когда
  папка тёплая (проверка это `File.Exists` на путь). С закэшированной папкой и
  `NUGET_FETCH=off` сеть не нужна вовсе. Плюс сервер компилятора и инкрементальный движок
  Xake: перекомпилируется только то, у чего изменился вход.
- **Аудит.** Всё, что попало в бинарник, перечислено в lock с хешами: из него строится SBOM,
  каждый вход проверяется по SHA-256 перед компиляцией, а prerequisites (SDK) названы явно, а не
  подразумеваются.

## 3. Что лежит в репозитории

| Файл | Коммитить? | Зачем |
|---|---|---|
| `build.fsx` | да | сама сборка |
| `locks/*.json` | **да** | проверенная запись того, что читает компилятор; ревьюится как код |
| исходники, `.csproj`, `Directory.Build.props` | да | входы |
| `global.json` | да | выбирает SDK для `dotnet fsi`, msbuild и компилятора SDK |
| `nuget.config` | да, если есть свои фиды | восстановление пакетов читает его |
| `.xake` (база сборки) | **нет** | у каждой машины своя |
| `obj/`, `out/`, `.packages/` | нет | продукты сборки и папки пакетов |

`global.json` с точной версией (пример):

```json
{ "sdk": { "version": "10.0.401", "rollForward": "disable" } }
```

## 4. Сценарий A: разработчик

### Скрипт

Одно описание компиляции на всё: сборку, обновление lock и проверку. Целиком (запускалось):

```fsharp
#r "nuget: Xake, 3.5.0.23"
#r "nuget: Xake.Hermetic.Dotnet, 0.1.0.22"
open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet

// одно описание компиляции на всё: сборку, обновление и проверку
let app = csc {
    src !!"src/*.cs"
    out (File.make "out/app.dll")
    resolve }

do xakeScript {
    var "CSC_TOOLSET" "4.12.0"      // компилятор из NuGet, а не из SDK (см. сценарий C)
    rules [
        "build" <== [ "out/app.dll" ]

        "out/app.dll" ..> recipe {
            let! c = app
            do! Lock.build "locks/app.json" c }

        "update-locks" => recipe {
            let! c = app
            do! Lock.record "locks/app.json" c }

        "check-locks" => recipe {
            let! c = app
            let! diff = Lock.verify "locks/app.json" c
            if not (List.isEmpty diff) then
                failwithf "locks/app.json устарел:\n%s" (String.concat "\n" diff) }
    ]
}
```

Что здесь не нужно писать:

- `targetfwk` не нужен. По умолчанию цель это .NET самого SDK (`net10.0` для SDK 10.0.x).
  Порядок: `targetfwk` в блоке, потом переменная `NETFX-TARGET`, потом фреймворк SDK. Ссылки
  берутся из targeting pack SDK (`<dotnet>/packs/Microsoft.NETCore.App.Ref/<версия>/ref/net10.0`),
  целиком, ничего не скачивается. `grefs` для `netN.0` не нужен.
- `targetfwk` пишут только когда цель намеренно .NET Framework (`targetfwk "net-4.6.2"`) или
  netstandard. Тогда ссылочные сборки приходят NuGet-пакетом, а для .NET Framework нужны `grefs`.
- Короткая форма без `resolve` тоже есть, если не нужны `update-locks`/`check-locks`:

```fsharp
"out/app.dll" ..> csc { src !!"src/*.cs"; out (File.make "out/app.dll"); lock "locks/app.json" }
```

F# устроен так же, `fsc { src !!"src/*.fs"; out (File.make "out/lib.dll") }` (запускалось). Без
`targetfwk` он берёт фреймворк SDK и `FSharp.Core.dll` из SDK, `ref` на FSharp.Core не нужен. Для
netstandard и .NET Framework FSharp.Core приходит пакетом (версия `FSHARP_CORE_VERSION`, по
умолчанию 8.0.100), для .NET Framework добавляется фасад `netstandard.dll`. `fsc { lock }` пока нет,
он в работе (0.2).

### Шаг 1. Первая сборка записывает lock

```
dotnet fsi build.fsx -- -- build
```

Lock нет. Xake разрешает настройки (раскрывает маски, находит компилятор и ссылки), хеширует
их, пишет `locks/app.json` и компилирует (запускалось). Lock коммитят вместе с изменением, которое
его создало. В нём, например:

```
"Compiler": { "Tool": "csc", "Path": "$(NuGetPackageRoot)/microsoft.net.compilers.toolset/4.12.0/tasks/netcore/bincore/csc.dll", "Sha256": "dd75...", "Version": "4.12.0-3.24572.7" },
"References": [ { "Path": "$(DotnetRoot)/packs/Microsoft.NETCore.App.Ref/10.0.12/ref/net10.0/Microsoft.CSharp.dll", "Sha256": "0ade..." }, ...
```

### Шаг 2. Следующие сборки компилируют lock

Ничего не менялось: цель пропускается как актуальная, lock не переписывается, даже время файла
то же (запускалось). Когда цель пересобирается, компилируется **записанная** запись lock, и её
хеши проверяются. Подменённый на диске файл валит сборку, хотя настройки не менялись.

### Шаг 3. Расхождение валит сборку

Добавили `src/Extra.cs` и пересобрали. Сборка падает, код выхода 2 (запускалось, ветка):

```
[ERROR] 2> 'app': the resolved compilation differs from the lock 'locks/app.json':
+ $(ProjectRoot)/src/Extra.cs
Update the lock deliberately: delete 'locks/app.json', or run the target that calls Lock.record "locks/app.json".
```

**[0.2]** В 0.1.0.22 путь в сообщении абсолютный (`+ /Users/.../src/Extra.cs`), в ветке с
токеном. Важно: новый файл под маской сам по себе цель не перезапускает (маска раскрывается, но не
отслеживается). Расхождение видно, когда цель пересобирается, или в `check-locks` в любой момент.

### Шаг 4. Обновить lock сознательно

```
dotnet fsi build.fsx -- -- update-locks
```

`Lock.record` хеширует и перезаписывает lock, ничего не компилирует. Дальше `build` проходит
(запускалось). Diff lock смотрят в том же PR, что и изменение кода. Другой способ: удалить lock
и собрать (не под CI).

### Шаг 5. Проверить, не компилируя

```
dotnet fsi build.fsx -- -- check-locks
```

Ничего не пишет и не компилирует. При расхождении падает с теми же строками diff, код 2
(запускалось):

```
[ERROR] Error 'locks/app.json устарел:
+ $(ProjectRoot)/src/Extra.cs'. See build.log for more details
```

Нет lock совсем: **[0.2]** `Lock.verify` падает с подсказкой (в 0.1 было `Neither rule nor file
is found`):

```
'app': the lock 'locks/app.json' does not exist, so there is nothing to verify. Record it (the target that calls Lock.record "locks/app.json", e.g. update-locks) and commit it.
```

Сравнение структурное: исходники, опции, define, пути ссылок и анализаторов, путь и версия
компилятора. Хеши сверяются при компиляции.

## 5. Сценарий B: msbuild-проект

Существующий `.csproj` не переписывают в `csc {}`. Его **импортируют**: `Project.import` один раз
спрашивает msbuild, что тот передал бы компилятору (design-time build: компилятор сообщает
командную строку вместо запуска), и пишет это в lock. Одна запись на пару (проект, фреймворк).
Кроме компиляции, lock хранит граф пакетов из `project.assets.json` с sha512 и список msbuild-файлов,
которые прочитал импорт, с SHA-256.

Скрипт (запускалось, ветка, проект `net8.0`, `global.json` на 8.0.425):

```fsharp
#r "nuget: Xake, 3.5.0.23"
#r "nuget: Xake.Hermetic.Dotnet, 0.1.0.22"
open Xake
open Xake.Hermetic.Dotnet

let lockPath = "locks/hello.json"

do xakeScript {
    rules [
        // единственное место, где пишется lock: запустить, посмотреть diff, закоммитить
        "update-locks" => recipe {
            let! options = getCtxOptions ()
            do! Project.import {
                Project.ImportOptions.Default with
                    Projects = [ "src/Hello/Hello.csproj" ]
                    Frameworks = [ "net8.0" ]
                    Output = System.IO.Path.Combine (options.ProjectRoot, lockPath) } }

        // сборка из lock: msbuild не запускается
        "build" => recipe {
            let! lock = Lock.load lockPath
            for entry in lock.Entries do
                do! Lock.compile entry }
    ]
}
```

Как это работает:

1. `update-locks` запускает restore и design-time build. `Output` абсолютный, потому что
   `Project.import` пишет файл относительно текущего каталога процесса.
2. `build` читает закоммиченный lock (`Lock.load` заодно делает его зависимостью цели) и
   компилирует каждую запись через `Lock.compile`: восстановление пакетов, проверка компилятора,
   подстановка `$(SourceRevisionId)` из `.git`, запуск компилятора с записанной командной строкой.
   Сборка кладётся туда же, куда msbuild (`/out:` записи, под `obj/xake/<framework>/`).
3. Нет lock: `build` падает с `Neither rule nor file is found for '.../locks/hello.json'`, код 2
   (запускалось). Это и есть защита для CI.

**Предупреждение про вариант «файловое правило».** Импорт можно повесить на правило
`"locks/hello.json" ..> recipe { ... }`. Тогда lock становится продуктом сборки. На свежем клоне нет
`.xake`, правило запускается снова и **перезаписывает закоммиченный lock**, на CI тоже, и
повторяет импорт после каждого коммита. Использовать только для lock, который не коммитят.

**Когда переимпортировать** (`update-locks`): изменился файл проекта, `Directory.Build.props` или
`.targets`, `Directory.Packages.props`, версия пакета, `global.json` (новый SDK значит новый
компилятор), список фреймворков.

Импорт предупреждает, если SDK не закреплён точно:

```
[WARN] 'Hello': the SDK is not pinned (none) -- the lock's compiler (5.9.0-1.26423.113, SDK 10.0.401) will drift with every SDK the machine picks; pin it with global.json { sdk: { version, rollForward: "disable" } }
```

`check-locks` для импортированного lock сравнивает свежий импорт с закоммиченным. Это ~15 строк
вокруг `Lock.diff`, они есть в [workflows.md](../../hermetic/workflows.md), раздел B. Ему нужны msbuild
и фиды. Самому `build` они не нужны. С `ProjectReference` между проектами нужен шаблон «одно правило
на выход» из [hermetic-guide.md](../../hermetic-guide.md), раздел 6.

## 6. Сценарий C: коллега / чистая машина

Коллега клонирует репозиторий и запускает `dotnet fsi build.fsx -- -- build`. Получает ту же
компиляцию. Это обеспечивает шаг восстановления внутри `Lock.build` и `Lock.compile`.

### Что восстанавливается

| Что | Восстанавливается? |
|---|---|
| компилятор из `CSC_TOOLSET` / `toolset` (`Microsoft.Net.Compilers.Toolset`) | да |
| ссылочные сборки .NET Framework (`Microsoft.NETFramework.ReferenceAssemblies.<moniker>`) и netstandard2.0 (`NETStandard.Library`) | да, пакет точной версии |
| `FSharp.Core` для fsc под netstandard / .NET Framework | да |
| пакеты ссылок и анализаторов импортированного lock (под `$(NuGetPackageRoot)`) | да |
| компилятор SDK (`$(DotnetRoot)/sdk/<v>/...`) | **нет** |
| targeting pack SDK для `netN.0` и анализаторы SDK (`$(DotnetRoot)/packs/...`) | **нет**, приходят с SDK |

Проверено на пустой папке (`NUGET_PACKAGES=<пусто>`): скрипт сценария A сам скачал
`microsoft.net.compilers.toolset/4.12.0` и собрал (запускалось).

### Откуда

Один `dotnet restore` синтезированного проекта `obj/xake/restore/<n>/restore.csproj` под корнем
проекта, по `PackageDownload` на пакет с точной версией (`[1.2.3]`). Следствия:

- Источники обычные для NuGet: `nuget.config` репозитория, пользователя и машины. Свой фид в
  репозитории работает.
- Учётные данные тоже обычные для NuGet (`packageSourceCredentials`, credential provider,
  `NuGetPackageSourceCredentials_<имя>`). Xake ничего не добавляет (не запускалось).
- `Directory.Build.*` и центральное управление пакетами для этого restore выключены.
- Один механизм на всё: ссылочные сборки фреймворков, toolset и пакеты lock идут через него же
  (`DotNetFwk.restorePackage`, затем `downloadPackages`). Раньше ссылочные сборки шли мимо, из
  временной папки; это исправлено.
- Неудачный restore оставляет проект на диске, его можно перезапустить руками.

### Что не восстановить: компилятор SDK

Если lock называет компилятор SDK, которого на машине нет:

```
'app': the lock names the compiler of SDK 8.0.425 (<путь>), which is not installed; install that SDK or re-import with the installed one
```

Два лекарства:

1. **`CSC_TOOLSET` на весь проект.** Одна строка в скрипте, и каждая запись lock называет
   компилятор из NuGet с фиксированной версией. Такой компилятор восстанавливается на любой машине:

   ```fsharp
   do xakeScript {
       var "CSC_TOOLSET" "4.12.0"
       rules [ ... ]
   }
   ```

   Порядок: `cscpath`, потом `toolset` в блоке, потом `CSC_TOOLSET`, потом SDK. Ставить в скрипте,
   а не в командной строке, чтобы у всех было одинаково.
2. **`global.json` с точной версией** (раздел 3). Он выбирает SDK для `dotnet fsi`, msbuild и
   компилятора SDK. Нет такого SDK: `dotnet` падает до старта Xake
   (`A compatible .NET SDK was not found. Requested SDK version: ...`).

Важно для цели `netN.0` по умолчанию: ссылки лежат в targeting pack SDK
(`$(DotnetRoot)/packs/Microsoft.NETCore.App.Ref/10.0.12/...` в нашем прогоне). `CSC_TOOLSET` это
не лечит. Другой патч SDK даст другой pack, и сборка упадёт с расхождением по путям ссылок.
Значит, для `netN.0` нужен и `global.json`. Полностью восстановимый lock (только NuGet) получается
для .NET Framework или netstandard2.0 вместе с `CSC_TOOLSET`.

### Проверки хешей

| Проверка | Когда | Что |
|---|---|---|
| SHA-256 файла | каждая компиляция | компилятор, каждая ссылка, каждый анализатор против lock |
| sha512 пакета | каждая компиляция, **[0.2]** и для уже лежащих пакетов | `contentHash` из `.nupkg.metadata` против sha512 в lock |

sha512 есть только у импортированного lock (там записан граф пакетов). У lock из `csc {}` графа
нет, его защищает SHA-256 каждого файла. Пакет без `.nupkg.metadata` даёт одно предупреждение
`sha512 not verified` и не считается ошибкой. Подмена файла (запускалось, ветка):

```
[ERROR] 2> ('app') hash mismatch:
<папка пакетов>/microsoft.net.compilers.toolset/4.12.0/tasks/netcore/bincore/csc.dll: expected dd75..., got 0057...
```

Если ничего не нужно: по одному `File.Exists` на путь, без процессов и сети.

## 7. Сценарий D: CI

### Флаг CI **[0.2]**

«Под CI» решает `Lock.underCi`, один раз на сборку:

- переменная скрипта `CI`, если задана: `-d CI=on|off` (или `true/yes/1`, `false/no/0`); другое
  значение это ошибка `CI='x': expected on or off`;
- иначе переменная окружения `CI`. Её ставят GitHub Actions, GitLab CI, Azure Pipelines.
  Настраивать ничего не надо.

`-d CI=off` на раннере ведёт себя как машина разработчика. `-d CI=on` локально репетирует CI.

### Что отличается на CI

| Ситуация | Разработчик | CI |
|---|---|---|
| нет lock | записать и собрать | **падение** до записи и компиляции |
| lock устарел | падение с diff | падение с diff (CI lock не обновляет) |
| lock совпадает | собрать записанное, проверить хеши | так же, пакеты восстанавливаются |

Нет lock под CI (запускалось, ветка, `CI=true`):

```
[ERROR] 2> 'app': the lock 'locks/app.json' is not there. Under CI a lock is never recorded: record it on a developer machine (build once, or run the target that calls Lock.record "locks/app.json", e.g. update-locks) and commit it.
```

`nofailonerror` это не смягчает, иначе CI прошёл бы без lock. С `CI=true` и `-d CI=off` lock
записывается (запускалось).

### Ещё три вещи

- **Сервер компилятора.** `csc` по умолчанию идёт через Roslyn-сервер (`VBCSCompiler`), он живёт до
  10 минут после последней компиляции. На выход не влияет. На постоянном раннере процесс переживёт
  задачу, поэтому на CI ставят `XAKE_CSC_SERVER=off` (`0`, `false`, `no` тоже). Переменная скрипта
  `CSC_SERVER` (`-d CSC_SERVER=off`) важнее окружения.
- **Кэш папки пакетов.** Кэшируют `~/.nuget/packages` (или `NUGET_PACKAGES`). Ключ: хеш
  `locks/*.json`, `build.fsx`, `global.json`. Запасной ключ: только префикс ОС, частичное попадание
  тоже полезно. Подменённый файл в кэше поймает SHA-256. `.xake` не кэшируют: свежая база
  заставляет каждую цель пересобраться и каждую проверку сработать.
- **Порядок `check-locks build` и `git status`.** Цели в командной строке идут по очереди, падение
  останавливает остальные. `check-locks build` не дойдёт до `build` без lock (запускалось:
  `Lock.verify` упал первым). Не склеивать через `;`: так цели идут параллельно. Последним шагом
  `git status --porcelain -- locks` ловит всё, что записало lock (например, прогон с `-d CI=off`).

### GitHub Actions (не запускалось)

```yaml
name: build
on: [push, pull_request]
jobs:
  build:
    runs-on: ubuntu-latest
    env:
      XAKE_CSC_SERVER: "off"
      DOTNET_NOLOGO: "1"
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json
      - uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('locks/*.json', 'build.fsx', 'global.json') }}
          restore-keys: nuget-${{ runner.os }}-
      - run: dotnet fsi build.fsx -- -- check-locks build
      - name: locks unchanged
        run: test -z "$(git status --porcelain -- locks)" || { git status --porcelain -- locks; exit 1; }
```

### GitLab CI (не запускалось)

GitLab кэширует только пути внутри проекта, поэтому папку пакетов переносят туда через
`NUGET_PACKAGES`. За ней идёт всё: токен `$(NuGetPackageRoot)`, restore, `dotnet fsi`. `.nuget/`
добавить в `.gitignore`. Тег образа должен совпадать с `global.json`.

```yaml
build:
  image: mcr.microsoft.com/dotnet/sdk:10.0.401
  variables:
    NUGET_PACKAGES: "$CI_PROJECT_DIR/.nuget/packages"
    XAKE_CSC_SERVER: "off"
  cache:
    key:
      files: [ global.json, locks/app.json ]   # не больше двух файлов
      prefix: nuget
    paths: [ .nuget/packages/ ]
  script:
    - dotnet fsi build.fsx -- -- check-locks build
    - test -z "$(git status --porcelain -- locks)" || { git status --porcelain -- locks; exit 1; }
```

## 8. Сценарий E: релиз

Релиз это сценарий D плюс четыре шага. У каждого своя страница, короткие примеры в
[hermetic-guide.md](../../hermetic-guide.md), разделы 8 до 10 (там они запускались).

**SBOM.** `Sbom.forAssembly (Roots.nugetRoot ()) entry assemblyPath` строит CycloneDX 1.6 только из
записи lock: пакеты графа, файлы ссылок с SHA-256 под своим пакетом, компилятор, SDK и анализаторы в
`formulation`. `Sbom.cycloneDx bom` даёт текст. У lock из `csc {}` графа пакетов нет, поэтому и
компонентов-пакетов в SBOM нет. Для nupkg: `Sbom.forPackage` (что видела сборка) и
`Sbom.forPackageScoped` (что получает клиент), проверка `Sbom.checkPackageScope`. Внутренних
префиксов по умолчанию нет, свои задают через `Sbom.PackageScope.idPrefixes [ "Acme." ]`. Вывод
детерминирован.

**Verify.** `Verify.authenticodeHash` одинаков у подписанного файла и его неподписанной сборки.
`Verify.compare` и `Verify.verdict` называют отличающиеся поля. Так доказывают, что выпущенный
бинарник собран из lock. Для совпадения байтов нужен тот же путь checkout (csc пишет абсолютные
пути в PDB) или `/pathmap`, и `/deterministic`: блок `csc {}` его сам не добавляет, нужно
`args ["/deterministic"]`.

**Pack.** `Pack.nupkg output nuspec files options` и `Pack.zip output entries options` пишут
детерминированные архивы: записи отсортированы, время одно (`SOURCE_DATE_EPOCH` или 1980-01-01),
идентификаторы из содержимого.

**Sign.** `sign { target ...; input ...; certificate ...; signer ... }` это правило с подменяемым
подписантом. В пакете есть только `Sign.fakeSigner` для проверки конвейера. `Sign.verifySameImage`
связывает подписанный файл с входом.

**Что архивировать с релизом**: тег коммита (в нём lock), сами lock как артефакт, SBOM на сборку
и на пакет, неподписанные выходы с их `Verify.authenticodeHash`, лог сборки.

## 9. Сценарий F: офлайн

Lock фиксирует каждый пакет по id, версии и хешу. Офлайн-сборке нужна только заранее
заполненная папка пакетов.

1. На машине с сетью заполнить папку: `NUGET_PACKAGES=/path/pkgs dotnet fsi build.fsx -- -- build`.
   Туда же лягут пакеты `Xake` и `Xake.Hermetic.Dotnet`, они нужны самому `dotnet fsi`.
2. Перенести папку на офлайн-машину.
3. Собирать с той же `NUGET_PACKAGES`. Чтобы пакеты lock точно не шли из сети, в блоке есть
   `norestore` **[0.2]** (не запускалось):

```fsharp
"out/app.dll" ..> csc {
    src !!"src/*.cs"
    out (File.make "out/app.dll")
    lock "locks/app.json"
    norestore                   // пакеты lock не скачивать
}
```

`norestore` идёт **после** `lock`. Там же можно поставить `packageroot ".packages"`: пакеты lock
читаются и восстанавливаются в папку репозитория, а не в кэш машины. Тогда офлайн нужно заполнить
и её. С `norestore` недостающий пакет даёт предупреждение, потом проверка хешей перечисляет все
недостающие файлы, до компиляции:

```
[WARN] 1 package(s) named by the lock are not in '<папка>' and automatic restore is off (Restore.Options.Enabled): <id> <версия>
[ERROR] ('app') hash mismatch:
<папка>/<id>/<версия>/.../X.dll: expected <sha256>, got missing
```

Для импортированного lock то же самое: `Restore.into ".packages"`, `Lock.loadWith
(Roots.packageRootOverride ...)` и `Lock.compileWith` с `Enabled = false` (пример в
[hermetic-guide.md](../../hermetic-guide.md), раздел 7).

Что офлайн ломается:

| Что | Почему |
|---|---|
| `Project.import`, `check-locks` импортированного lock | msbuild восстанавливает проект из фидов |
| `toolset` / `CSC_TOOLSET`, ссылочные сборки .NET Framework/netstandard, `FSharp.Core` для fsc | разрешаются до чтения lock и всегда в кэш машины; `norestore` и `packageroot` на них не действуют. Поэтому папка из шага 1 должна быть именно `NUGET_PACKAGES` |
| `#r "nuget: ..."` в `dotnet fsi` | пакеты Xake должны быть в папке |

## 10. Если что-то пошло не так

| Сообщение | Причина | Что делать |
|---|---|---|
| `'<name>': the resolved compilation differs from the lock '<path>':` + строки diff | изменились настройки, файлы под маской, компилятор или SDK | посмотреть diff; `update-locks` и закоммитить, или откатить изменение |
| `<path> устарел:` (свой `check-locks`) | то же, найдено без компиляции | то же |
| `'<name>': the lock '<path>' is not there. Under CI a lock is never recorded: ...` | CI, lock не закоммичен | записать lock у себя (`build` или `update-locks`), закоммитить; `-d CI=off` вернёт поведение разработчика |
| `'<name>': the lock '<path>' does not exist, so there is nothing to verify. ...` | `check-locks` без lock | то же |
| `Neither rule nor file is found for '<root>/locks/<x>.json'` | `Lock.load` импортированного lock, lock не закоммичен или путь неверен | `update-locks`, закоммитить |
| `('<name>') hash mismatch:` / `<path>: expected <a>, got <b>` | файл на диске не тот: другая сборка пакета, битый кэш, пересобранная ссылка | удалить каталог пакета и пересобрать |
| `... expected <sha256>, got missing` у многих файлов | restore выключен, или lock прочитан с другой папкой пакетов, чем восстановлен | одна папка для чтения и restore, или `NUGET_PACKAGES` |
| `'<name>': the lock names the compiler of SDK <v> (<path>), which is not installed; ...` | lock сделан на другом SDK | поставить SDK из `global.json`, или `CSC_TOOLSET` и перезаписать lock |
| `'<name>': the compiler <path> is not available and restoring <id> <v> did not provide it` | toolset не скачался | проверить фид; удалить недокачанную папку |
| `toolset <id> <v>: the package folder <dir> has no compiler at <path> (a partial restore? delete the folder and rebuild)` | битая папка пакета toolset | удалить `<dir>`, пересобрать |
| `restoring N package(s) into '<folder>' failed with exit code <n> (see '<project>'): <ids>` | `dotnet restore` упал: фид недоступен, учётные данные, пакета нет | починить источник; перезапустить оставленный `restore.csproj` руками |
| `('<name>') restoring the packages the lock names failed:` + `<id> <v>: not restored (expected it at '<dir>')` | restore прошёл, но пакета нет | проверить фид в `nuget.config`; вывод NuGet в логе на уровне verbose |
| `<id> <v>: expected sha512 <a>, got <b>` | фид отдал другой nupkg, чем записан | пакет перевыпущен; выяснить почему, до переимпорта |
| `<id> <v>: sha512 not verified, '<file>' does not exist ...` (предупреждение) | кэш без `.nupkg.metadata` (старый NuGet) | можно игнорировать; SHA-256 файлов всё равно проверен |
| `N package(s) named by the lock are not in '<folder>' and automatic restore is off` (предупреждение) | `norestore` / `Enabled = false` | заполнить папку или включить restore |
| `reference assemblies for 'net8.0' are not available: no targeting pack under '<dir>' has ref/net8.0 (install a net8.0 SDK, or target the SDK's own framework)` | `targetfwk` старше SDK, pack не установлен | поставить нужный SDK или убрать `targetfwk` |
| `global.json (<file>) asks for SDK <v>, which is not installed in '<root>'; using the newest SDK (<name>)` (предупреждение) | закреплённого SDK нет, Xake взял новейший | поставить SDK из `global.json` |
| `A compatible .NET SDK was not found. Requested SDK version: <v>` (от `dotnet`, до Xake) | то же, для самого `dotnet fsi` | поставить SDK (`setup-dotnet` с `global-json-file`, нужный тег образа) |
| `'<name>': csc needs a target framework: ...; the SDK's own framework could not be determined` | SDK не найден или не сказал свой фреймворк | проверить установку SDK; или задать `targetfwk` |
| `'<name>': the lock needs $(SourceRevisionId) but no git repository was found at or above '<dir>' ...` | импортированный lock с SourceLink собирается вне git | собирать в checkout, не в `git archive` |
| `project '<name>' is in the lock for N frameworks (...); ask for one with Lock.entryFor` | lock на несколько фреймворков | `Lock.entryFor framework name` |
| `CI='<x>': expected on or off` | неверное значение `-d CI=` | `on` или `off` |
| `the SDK is not pinned (...) -- the lock's compiler (...) will drift ...` (предупреждение импорта) | нет точного `global.json` | `global.json` с `rollForward: "disable"` |

## 11. Что ещё не сделано

- **F# в lock** (`fsc { lock }`, F# в импорте): в работе (0.2). Сейчас lock есть только у C#.
- **Восстановление SDK.** Компилятор и targeting pack SDK не скачиваются. Идея есть (SDK как ещё
  один восстановимый вход), решения нет. Пока лечится `global.json` и `CSC_TOOLSET`.
- **Настоящий подписант.** В пакете только `Sign.fakeSigner`; Azure Trusted Signing или хранилище
  сертификатов подключает пользователь.
- **Windows.** Новые части (CI-флаг, `packageroot`/`norestore`, sha512 уже лежащих пакетов, цель
  по умолчанию из SDK) проверены только на macOS.
- **YAML для CI** выше ни разу не запускались. Первый реальный прогон и будет проверкой.
- **Импорт**: нет `check-locks` в одну строку (сравнение документов целиком) и нет сборки всего
  lock с учётом `ProjectReference` одной функцией; PR открыты.
- **Восстанавливается только то, что читает компилятор.** Для тестов и публикации из той же папки
  нужен отдельный `Restore.download` остального графа.
