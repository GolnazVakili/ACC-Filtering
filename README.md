# ACC server-side copy

Standalone .NET 8 console app that copies Navisworks cache files (`.nwc`) from one Autodesk Construction Cloud folder to another in the **same project**.

The copy is done by the Autodesk Platform Services Data Management API on the server. The app does not download or upload file bytes.

Dry-run is the default. Nothing is copied until you pass `--execute` and confirm.

## What you enter at startup

The app asks for these every run:

- APS client ID (optional, written to the log only)
- APS access token (masked). Paste the three-legged token for **this** project.
- Hub ID
- Project ID
- Excel file path
- Source folder path in ACC
- Destination folder path in ACC

Press Enter to accept a saved client ID, hub ID, project ID, or source path when one is shown. The access token has no saved value.

The app does **not** read `APS_ACCESS_TOKEN`, `APS_CLIENT_ID`, `APS_HUB_ID`, or `APS_PROJECT_ID` from the environment. A token left behind by another project is not used. Do not put the token in `appsettings.json`.

Hub and project IDs get a `b.` prefix when you omit it. That is the form the Data Management API expects.

Folder names are separated by `>`:

```text
02.Shared External > USEP > All > NWC > 03.03.01 NWC
04.Coordination > Projectwide > NWC
```

If the first folder is not a top-level folder, the app looks one level down (usually inside `Project Files`). If a segment is missing, it prints the folders at that level and asks again.

## Prerequisites

1. An APS app that is added as a **Custom Integration** in ACC Account Admin.
2. A three-legged user token whose scopes include `data:read`, `data:write`, and `data:create`. Creating and copying items calls endpoints that require `data:create`. Listing folders requires `data:read`.
3. That user needs **View + Download** on the source folder and **Upload + Edit** on the destination folder.
4. .NET 8 SDK.

### Hub ID and project ID

Call the Project API with the same user token:

- `GET https://developer.api.autodesk.com/project/v1/hubs`
- `GET https://developer.api.autodesk.com/project/v1/hubs/{hubId}/projects`

Use the `id` values. If they are plain GUIDs, you can paste them as-is; the app adds `b.`.

## Configure the source folder

`appsettings.json` holds the default source path:

```text
02.Shared External > USEP > All > NWC > 03.03.01 NWC
```

Optional non-secret defaults go in `appsettings.Local.json` (gitignored). Copy `appsettings.Local.json.example`. Leave the token out of both files.

## Excel file

The first sheet needs a column named `Model Name`. Values are model names without an extension, for example `A49_A_DM_ARCH_C`. The ACC file `A49_A_DM_ARCH_C.nwc` matches. Empty rows are skipped. Duplicate names are skipped after the first one, with a warning.

A name that is not an exact match can show a closest ACC name in the report when the similarity is at least 0.85. That suggestion is never copied.

## Web app

The same copy runs in a local browser app. It listens only on this machine.

```powershell
dotnet run --project web/AccFiltering.Web
```

Open http://127.0.0.1:5080. Enter the token, hub, project, Excel file, and folder paths there. Leave **Copy on the server** unchecked for a dry run.

## Console app

From this folder:

```powershell
dotnet run
```

That is a dry run. Then:

```powershell
dotnet run -- --execute --limit 2
```

Check those two files in the destination folder. Then:

```powershell
dotnet run -- --execute
```

Other switches:

| Switch | Effect |
| --- | --- |
| `--dry-run` | Plan and report only. This is the default. |
| `--execute` | Copy after you confirm. |
| `--overwrite` | If the destination name exists, create a new version with `copyFrom` instead of skipping it. |
| `--limit N` | Copy at most N files. |
| `--verbose` | Log the HTTP method, URL, and status. Headers and the token are never logged. |

A published build:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

## Report

The app writes `copy_report_yyyyMMdd_HHmm.csv` in the working directory:

`ExcelName, MatchedName, SourceItemId, SourceVersionId, Status, DestinationItemId, Error`

Status values: `COPIED`, `SKIPPED_EXISTS`, `NOT_FOUND`, `FAILED`, plus `DRY_RUN` and `NOT_ATTEMPTED` for the plan. The process exits with a non-zero code when any row is `FAILED`.

## API contract

Checked against the Data Management OpenAPI in [autodesk-platform-services/aps-sdk-openapi](https://github.com/autodesk-platform-services/aps-sdk-openapi/blob/main/datamanagement/datamanagement.yaml) (`createItem` and `createVersion`).

Server-side copy is a query parameter, not a JSON relationship:

```http
POST /data/v1/projects/{projectId}/items?copyFrom={url-encoded tip version URN}
Content-Type: application/vnd.api+json
```

The body still includes `jsonapi.version`, `relationships.tip` with local id `1`, `relationships.parent`, and one included version whose `id` is `1` and whose `attributes.name` is the file name. The included version does not send `storage` or `copyFrom`. Extension types for a Project Files folder are `items:autodesk.bim360:File` and `versions:autodesk.bim360:File`, each with version `1.0`.

A new version of an existing item uses:

```http
POST /data/v1/projects/{projectId}/versions?copyFrom={url-encoded tip version URN}
```

The version body points `relationships.item` at the existing destination item and also omits `storage`.

Plans folders need `items:autodesk.bim360:Document`. This tool refuses those destinations because `.nwc` files belong under Project Files. A `409` is recorded as `SKIPPED_EXISTS`. A `401` stops the run. A `403` means the APS app is missing from ACC Custom Integrations, or the user is missing folder permission.

Copies stay in one project. The API cannot copy a file while it is still processing.

## Tests

```powershell
dotnet test
```
