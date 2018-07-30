# PSWorkerPrototype
Prototype for Azure Functions PowerShell Language Worker

## Steps

1. Modify `DefaultExecutablePath` in `worker.config.json` (to something like this `"C:\\Program Files\\dotnet\\dotnet.exe"`)
2. `cd path/to/PSWorkerPrototype`
3. `dotnet build`
4. `cp bin\Debug\netcoreapp2.1 ~\AppData\Roaming\npm\node_modules\azure-functions-core-tools\bin\workers`
5. `Remove-Item -Rec -Force ~\AppData\Roaming\npm\node_modules\azure-functions-core-tools\bin\workers\powershell`
5. `Rename-Item ~\AppData\Roaming\npm\node_modules\azure-functions-core-tools\bin\workers\netcoreapp2.1 powershell`