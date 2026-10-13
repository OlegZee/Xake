# ActiveReports, полный состав: кикофф (2026-10-13)

Цель этапа: расширить AR-эксперимент с 15 core-библиотек (dataengine + page) до **всего продукта,
включая десктоп-дизайнер** (`ar-net-win`). Три шага, по порядку:

1. собрать максимум штатными средствами (`dotnet build` / msbuild / их Cake) — **сделано на macOS**,
   см. [macos-build.md](macos-build.md);
2. воспроизвести это через Xake (`Project.import` + locks) — не начато;
3. убедиться, что то, что собираем под .NET Framework, работает под Windows (виртуалка пользователя).

Файлы здесь: [inventory.md](inventory.md) — репозитории, граф, тулинг, TFM, риски (317 строк,
§0 «Key findings» читать первым); [macos-build.md](macos-build.md) — пробная сборка, таблица по
94 проектам, команды, которые сработали; `nuget-public.config`, `macos-probe.targets` — два пробника
из §4 отчёта (копии; оригиналы и логи в `~/Projects-work/ar/xake-ar/full/`).

## Что нашли

- **Confluence/Bitbucket через MCP недоступны** (Rovo/CQL: 403 «app is not installed»). Вместо этого:
  все репозитории склонированы с Bitbucket по https, инженерная документация — в репозитории
  `ar-net-docs` (`structure.md` описывает мета-репозиторий).
- **Мета-репозиторий `ar-net-meta`** (`~/Projects-work/ar/ar-net-meta`, ветка `develop/20`, HEAD
  a9895a5 от 2026-10-13) — штатный способ собрать всё из исходников: 15 сабмодулей на запиненных
  коммитах, `ar.meta.{all,win,web,vs}.sln`, `Directory.build.props` с `LocalBuild=true`, при котором
  межрепозиторные зависимости становятся `ProjectReference` на соседние папки. Пины page 808ef9f249 и
  dataengine 25b7e48c9a совпадают с теми, что использовал предыдущий эксперимент. Это продуктовая
  линия **20.2.2**. `develop/21` пинит релизные ветки core (page 4.6.0-alpha-16325, dataengine 5.5.1),
  тогда как `develop` core уже на 4.7/5.6 — не смешивать.
- **Реальный CI — TeamCity**, конфигурации в репозиториях нет. `bitbucket-pipelines.yml` везде только
  AI-ревью и gitleaks. Локальная сборка дизайнера: `dotnet cake` из `ar-net-win/scripts/`
  (`build.cake`, Cake.Tool 5.0.0, vswhere → полный MSBuild из VS Build Tools 2022; обфускация Babel и
  подпись AzureSignTool — только шаги Cake, по умолчанию `auto` = выкл. локально).
- **TFM десктопа: `net472` и `net8.0-windows`** (+ `net8.0` для не-UI). **net462 нигде нет**
  (кроме двух демо-приложений лицензирования). Core: `netstandard2.0;net472`.
- **Приватный фид `ar-dev`** (Azure DevOps, `%AA_AR_TOKEN%`) нужен даже при `LocalBuild=true`:
  `MESCIUS.DataVisualization.Chart 0.6.1` (reportitems-chart → `ActiveReports.csproj` → весь
  `ar-net-win`), `Editor.Net 1.55.40` (qwhale), `QueryDesigner 2.0.3-alpha` (vqd), `SpreadBuilder
  3.0.4-alpha` (Export.Excel/Word). В локальном NuGet-кеше их нет, на nuget.org нет. Репозитории
  qwhale/spreadbuilder/vqd склонированы, но не сабмодули; vqd сам тянет приватный DataProviders.
- **Ловушка**: мета-файл называется `Directory.build.props` (строчная b), сабрепо импортируют
  `Directory.Build.props` через `GetPathOfFileAbove` — на case-sensitive ФС (Linux CI) импорт не найдётся.

## Пробная сборка на macOS (SDK 8.0.425, csc 4.11)

| | проектов |
|---|---|
| в `ar.meta.win.sln` | 94 csproj + 7 shproj |
| собрались чистым `dotnet build -p:LocalBuild=true -p:EnableWindowsTargeting=true` | 51 |
| собрались с двумя пробниками (nuget.org-only конфиг для DS.Documents; `GenerateResourceUsePreserializedResources` для resx) | 55 |
| не прошли restore из-за приватного фида | 39 (38 — из-за DataVisualization.Chart) |
| не собрались из-за macOS как таковой | 0 |

Полная пересборка 55 проектов: 28.8 с. Из `ar-net-win` собрался только `ActiveReports.Interop`
(net472). WinForms компилируется на macOS; **единственное отличие от Windows** — net472 resx с
бинарными ресурсами требует `System.Resources.Extensions` (MSB3822/3823), что добавляет runtime-зависимость
и ломает побайтовое совпадение с Windows-сборкой. WPF/XAML, COM interop, licx — не проверены (не дошли
до компиляции).

## Решения, которые нужны от пользователя

1. **`AA_AR_TOKEN`** для фида `ar-dev` — без него дизайнер не собирается ни на одной ОС. Альтернатива:
   собрать qwhale/spreadbuilder/vqd из исходников в локальный фид (inventory §4.3, не проверено), но
   `MESCIUS.DataVisualization.Chart` исходников в мете нет.
2. **Какой .NET Framework проверять под Windows**: продукт целится в **4.7.2**, не 4.6.2. Для
   Windows-проверки нужен taргетинг-пак 4.7.2 (VS Build Tools 2022: .NET desktop build tools) + SDK 8.
3. Что считать «воспроизвести»: `ar.meta.win.sln` целиком (с WPF/VSIX — вне csc) или «всё, что
   является чистым C#» (core + non-UI win libs + WinForms без XAML).

## Следующие шаги

- **Шаг 1 на Windows**: на виртуалке `git clone --recurse-submodules` мета `develop/20` (или скопировать
  `~/Projects-work/ar/ar-net-meta`), VS Build Tools 2022 + SDK 8.0.4xx, `dotnet build ar.meta.win.sln
  -p:LocalBuild=true -c Release` с токеном; затем `cd ar-net-win/scripts && dotnet cake` для штатного
  результата (нужен манифест/глобальный Cake.Tool 5.0.0). Сохранить `bin/` как эталон для сравнения.
- **Шаг 2 (Xake)**: расширить `ar-build-2.fsx` на `ar.meta.win.sln`: один `Import.descriptions` по
  всем csproj с `LocalBuild=true; EnableWindowsTargeting=true`; закрыть гэпы импортёра из
  inventory §5: `.resx` с типизированными ресурсами (resgen на net462-пути, см. `Impl.compileResx`),
  Cake-генерируемый `VersionInfo.cs`, `.projitems` по `LicenseType`, матрица
  `net472;net8.0-windows;net8.0`, CPM с transitive pinning; XAML — вне csc, либо пропускать, либо брать
  `obj/*.g.cs` от msbuild как вход.
- **Шаг 3**: сравнить Xake-сборку net472 с Windows-эталоном (`Verify`), затем запустить дизайнер на
  виртуалке.
