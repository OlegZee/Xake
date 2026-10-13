# ActiveReports: как собирается десктоп (инструкция для воспроизведения в Xake)

Факты из [cake.md](cake.md) и [matrix.md](matrix.md); дата 2026-10-13, продуктовая линия 20.2.x.

## 0. Источник

`~/Projects-work/ar/ar-net-meta`, ветка `develop/20`, 15 сабмодулей на пинах. Цель —
`ar.meta.win.sln`: 94 csproj (cores + `ar-net-win` + chart + ai-assistance + dev-apps + тесты),
`LocalBuild=true` из мета `Directory.build.props` (все межрепозиторные зависимости — `ProjectReference`).
Штатный Cake собирает иначе: каждый репозиторий отдельно, `ar.win.sln`, `LocalBuild=false`, core из
nupkg версий `references.props`. Для Xake берём мету.

Ключи фида: `source ~/Projects-work/.env` (`AA_AR_TOKEN`) в начале каждой терминальной сессии.

## 1. Матрица

| ось | значения | примечание |
|---|---|---|
| Brand | `` (= MESCIUS), `GCCN` | `MESCIUS_JP` только у win-уровня, у core нет `MESCIUS_JP.props`; CI: EN, JP, GCCN |
| Configuration | `Release`; `Debug` | CI собирает только Release (захардкожено в Cake и nuspec) |
| TFM | `net472`, `net8.0-windows`, `net8.0`, `netstandard2.0` | по проекту, см. cake.md §1.8 |

Brand меняет: props-файл (`MESCIUS[_EN].props`/`GCCN.props`), `VersionPrefix` (EN 20.2.2, GCCN 20.2.1,
JP 20.2.0), `AssemblyNamePrefix` (`MESCIUS.ActiveReports.*` / `GCCN.ActiveReports.*`),
`DefineConstants` (`MESCIUS` | `GCCN` | `MESCIUS;LICENSE_JP`), `LicenseType` (JP только для JP, иначе EN),
набор пакетов в restore (GCCN тянет `GcDocs.*` вместо `DS.Documents.*`), культуру сателлитов.
Смена бренда перекомпилирует всё (196 запусков csc, 0 пропусков); restore тоже зависит от бренда.

## 2. Команды (macOS, SDK 8.0.425)

```bash
set -a; source ~/Projects-work/.env; set +a
cd ~/Projects-work/ar/ar-net-meta
F=~/Projects-work/ar/xake-ar/full/scripts
P="-p:LocalBuild=true -p:NuGetAudit=false -p:EnableWindowsTargeting=true \
   -p:RestoreConfigFile=$F/nuget-feed.config -p:CustomAfterMicrosoftCommonTargets=$F/macos-probe.targets"
# Brand: пусто для MESCIUS, GCCN для GCCN; Cfg: Debug|Release
dotnet restore ar.meta.win.sln $P -p:Brand=$BRAND
dotnet build   ar.meta.win.sln $P -p:Brand=$BRAND -c $CFG --no-restore -m
```

Три `-p:` вне штатной сборки: `EnableWindowsTargeting` (WinForms/WPF TFM вне Windows);
`RestoreConfigFile` — плоский NuGet.config, потому что внутри меты дочерние `<clear/>` + унаследованный
`packageSourceMapping` ломают restore (`MESCIUS.DataVisualization.Chart` → NU1100/NU1101);
`macos-probe.targets` — `GenerateResourceUsePreserializedResources` + `System.Resources.Extensions` для
net472 resx с бинарными ресурсами и CS9057 как warning. На Windows с MSBuild из VS не нужен ни один из
трёх: Cake намеренно берёт `MSBuild.exe`, чтобы net472 **не** ссылался на `System.Resources.Extensions`.
Поэтому macOS-сборка net472 не побайтово равна Windows-сборке.

## 3. Эталонные тайминги (M5 Pro 18 ядер, чистый obj/bin, тёплый NuGet-кеш, два прогона)

| ячейка | restore | чистая сборка | no-op | сборок (dll+exe) net472/net8.0/net8.0-win/ns2.0 | warn/err |
|---|---|---|---|---|---|
| MESCIUS / Debug | 3.4–3.6 с | 32.8–33.5 с | 4.9–5.8 с | 93/42/26/24 = 185 | 52 / 0 |
| MESCIUS / Release | 3.3–3.4 с | 41.3–44.0 с | 5.4–5.7 с | 185 | 54 / 0 |
| GCCN / Debug | 3.2–3.3 с | 31.6–32.0 с | 5.3–6.6 с | 92/41/26/24 = 183 | 52 / 4 |
| GCCN / Release | 3.2–3.8 с | 45.8–51.7 с | 6.0–6.1 с | 183 | 54 / 4 |

Все четыре ячейки подряд без очистки (смена бренда/конфигурации на тёплом дереве): 130.7 с.
No-op: csc не запускается ни разу. 4 ошибки GCCN — только тестовый проект
`ar-net-core-page/tests/exports/Svg.Tests` (CS0433: `GcGraphics` есть и в `DS.Documents.Imaging`, и в
`GcDocs.Imaging`); все продуктовые сборки GCCN собираются. Внимание: Spotlight (`mds_stores`) после
каждой сборки держит load высоким; перед замерами ждать load < 6.

## 4. Что делает штатный Cake поверх `dotnet build` (ar-net-win, `scripts/build.cake`, 1379 строк)

Цепочка `MainTask`: SetupFolders → [Clean] → PrepareLocalizationResources → BuildLocalizationResources →
**Build** → RunTests → Obfuscate → Pack. Для Xake важны:

1. **Версия**: `Version = VersionPrefix[-suffix]`, `FileVersion = VersionPrefix.0`; suffix `alpha-N` на
   develop, `beta-N` на release/hotfix, пустой на master (+`-ja`/`-cn` по бренду). Константы
   переписываются в копии `VersionInfo.cs` в `build/temp`, проекты компилируют `$(BuildTmpPath)\VersionInfo.cs`;
   `AssemblyNames.cs` берёт `VersionInfo.Version` в строки strong-name.
2. **Свойства** на `MSBuild.exe -m:4 ar.win.sln`: `Brand`, `LicenseType`, `BuildOutPath=build/`,
   `BuildTmpPath=build/temp`, `XmldocsOutput=build/XmlDocs`, `InheritDocEnabled`, `NUnit=true`, `Version`,
   `FileVersion`, `AdditionalResourcesDir`, `Configuration=Release`, `LocalBuild=false`.
3. **Компиляция** (общее для всех проектов): LangVersion 12, `TreatWarningsAsErrors` (кроме NU1901-4, CS9057),
   strong name `activereportskey.snk` без delay sign, `RuntimeIdentifier win-x64` у `ActiveReports`,
   `Licensing.<LicenseType>*.projitems` подключаются по `LicenseType`, resx → `.resources` с `LogicalName`,
   `AdditionalResourcesDir/<Assembly>/*` встраиваются как `EmbeddedResource` (только net472).
4. **Порядок**: cores (dataengine → page → document, dataproviders, reportitems-chart, chart) →
   `ActiveReports` → `ActiveReports.Win`, `Interop` → `Export.*` → `Export.*.Win` → `Viewer.Common` →
   `Viewer.Win`, `Viewer.Wpf` → `Advanced.Win` → `Design.Win` (+ `Editor.Net.dll`) → dev-apps → тесты.
5. **Пост-шаги**: `build/net472` → `build/Applications`; `ActiveReports.config` по бренду; удалить `App.config`
   и чужие культуры; сгенерировать `DefaultWPFViewerTemplates.xaml`; записать `ArBuild.props` (версии для
   web-репозиториев). Сателлиты (JP/GCCN) — из `ar-net-localization` (`al.exe` для net472, `dotnet` для net8.0).
6. **Pack**: `dotnet pack --no-build` по 13 nuspec с токенами (`PackageVersion`, `JaSuffix`, `SatellitesPath`,
   `BuildOutPath=Applications`), `lib/<tfm>/`, `buildTransitive/`, `tools/`; имена в нижнем регистре.
   Публикация — в TeamCity, в скриптах её нет.
7. **Вне csc** (отдельные инструменты или Windows): XAML → BAML (`Viewer.Wpf`, `WpfViewer`), `ResXFileRef`,
   Babel 10.9 (между тестами и pack, нужна лицензия), AzureSignTool (в chart; у win нет), `al.exe`/`resgen`,
   VSIX (`ar-net-vs`, Nuke + VSSDK), WiX-инсталлятор (`ar-net-installer`, Cake 4 + WiX 3.14.1), npm-бандлы
   web, нативный `Interop.C++`.

Остальные репозитории используют тот же Cake-шаблон со своими цепочками (cake.md §2); у core
`Release_NoTests`, у web `Release_Ja`. Единственный декларативный граф CI — `product.yaml` в корне меты
(варианты en/zh/ja, `build_units`).

## 5. Открытое

Обфусцируются/подписываются ли копии в `build/Applications` уже в TeamCity; что меняет `NUnit=true`;
точный порядок шагов TeamCity между репозиториями.
