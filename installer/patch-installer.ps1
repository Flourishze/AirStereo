function Add-PreviousInstallPathSupport {
    param(
        [Parameter(Mandatory = $true)]$Assembly
    )

    $module = $Assembly.MainModule
    $setupType = $module.Types | Where-Object Name -eq 'AirStereoSetup'
    $wizard = $setupType.NestedTypes | Where-Object Name -eq 'WizardForm'
    if ($null -eq $setupType -or $null -eq $wizard) {
        throw 'Expected AirStereoSetup/WizardForm installer types were not found.'
    }

    $existing = $setupType.Methods | Where-Object Name -eq 'GetPreviousInstallLocation' | Select-Object -First 1
    if ($null -ne $existing) {
        throw 'Installer already contains GetPreviousInstallLocation; refusing to patch twice.'
    }

    # The setup executable targets .NET Framework 4.x. Do not import these types
    # from the build machine's current .NET runtime: on .NET 8/10 that would add
    # a Microsoft.Win32.Registry.dll reference which is not present on older PCs.
    # Reuse the mscorlib-scoped references already present in the template.
    $registerMethod = $setupType.Methods | Where-Object Name -eq 'RegisterUninstall' | Select-Object -First 1
    $registryRootField = $registerMethod.Body.Instructions |
        Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldsfld -and $_.Operand.FullName -like '*Registry::CurrentUser*' } |
        Select-Object -First 1
    $registryType = $registryRootField.Operand.FieldType
    $registryRoot = $registryRootField.Operand
    $dispose = $registerMethod.Body.Instructions |
        Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Callvirt -and $_.Operand.FullName -like 'System.Void System.IDisposable::Dispose()' } |
        Select-Object -First 1
    $exceptionType = $setupType.Methods | ForEach-Object {
        if (-not $_.HasBody) { return }
        $_.Body.Instructions |
            Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.FullName -like 'System.String System.Exception::get_Message()' } |
            Select-Object -First 1
    } | Select-Object -First 1
    $exceptionType = if ($exceptionType) { $exceptionType.Operand.DeclaringType } else { $null }
    $stringType = $module.TypeSystem.String
    $objectType = $module.TypeSystem.Object

    $openSubKey = [Mono.Cecil.MethodReference]::new('OpenSubKey', $registryType, $registryType)
    $openSubKey.HasThis = $true
    $openSubKey.Parameters.Add([Mono.Cecil.ParameterDefinition]::new('name', [Mono.Cecil.ParameterAttributes]::None, $stringType))
    $getValue = [Mono.Cecil.MethodReference]::new('GetValue', $objectType, $registryType)
    $getValue.HasThis = $true
    $getValue.Parameters.Add([Mono.Cecil.ParameterDefinition]::new('name', [Mono.Cecil.ParameterAttributes]::None, $stringType))
    $isNullOrWhiteSpace = ($setupType.Methods | Where-Object Name -eq 'InstallPayload' | Select-Object -First 1).Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.FullName -like 'System.Boolean System.String::IsNullOrWhiteSpace(System.String)' } |
        Select-Object -First 1
    $directoryExists = ($wizard.Methods | Where-Object Name -eq 'BrowseClicked' | Select-Object -First 1).Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.FullName -like 'System.Boolean System.IO.Directory::Exists(System.String)' } |
        Select-Object -First 1
    $isNullOrWhiteSpace = if ($isNullOrWhiteSpace) { $isNullOrWhiteSpace.Operand } else { $null }
    $directoryExists = if ($directoryExists) { $directoryExists.Operand } else { $null }

    if ($null -eq $registryRoot -or $null -eq $registryType -or $null -eq $openSubKey -or $null -eq $getValue -or $null -eq $dispose -or
        $null -eq $exceptionType -or
        $null -eq $isNullOrWhiteSpace -or $null -eq $directoryExists) {
        throw 'Could not resolve installer registry/path APIs.'
    }

    $previousMethod = [Mono.Cecil.MethodDefinition]::new(
        'GetPreviousInstallLocation',
        [Mono.Cecil.MethodAttributes]::Private -bor [Mono.Cecil.MethodAttributes]::Static -bor [Mono.Cecil.MethodAttributes]::HideBySig,
        $stringType)
    $setupType.Methods.Add($previousMethod)
    $body = $previousMethod.Body
    $body.InitLocals = $true
    $keyPath = [Mono.Cecil.Cil.VariableDefinition]::new($stringType)
    $key = [Mono.Cecil.Cil.VariableDefinition]::new($registryType)
    $rawValue = [Mono.Cecil.Cil.VariableDefinition]::new($objectType)
    $previous = [Mono.Cecil.Cil.VariableDefinition]::new($stringType)
    $result = [Mono.Cecil.Cil.VariableDefinition]::new($stringType)
    $body.Variables.Add($keyPath)
    $body.Variables.Add($key)
    $body.Variables.Add($rawValue)
    $body.Variables.Add($previous)
    $body.Variables.Add($result)

    $il = $body.GetILProcessor()
    $tryStart = [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldnull)
    $il.Append($tryStart)
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Stloc, $result))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldstr, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\AirStereo'))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Stloc, $keyPath))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldsfld, $registryRoot))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldloc, $keyPath))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Callvirt, $openSubKey))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Stloc, $key))

    $noKey = [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Nop)
    $invalid = [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Nop)
    $returnValue = [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Nop)
    $returnNull = [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Nop)
    $catchStart = [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Pop)

    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldloc, $key))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Brfalse, $noKey))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldloc, $key))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldstr, 'InstallLocation'))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Callvirt, $getValue))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Stloc, $rawValue))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldloc, $rawValue))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Isinst, $stringType))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Stloc, $previous))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldloc, $previous))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call, $module.ImportReference($isNullOrWhiteSpace)))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Brtrue, $invalid))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldloc, $previous))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call, $module.ImportReference($directoryExists)))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Brfalse, $invalid))
    $il.Append($returnValue)
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldloc, $key))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Callvirt, $dispose.Operand))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldloc, $previous))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Stloc, $result))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Leave, $returnNull))
    $il.Append($invalid)
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldloc, $key))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Callvirt, $dispose.Operand))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Leave, $returnNull))
    $il.Append($noKey)
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Leave, $returnNull))
    $il.Append($catchStart)
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Leave, $returnNull))
    $il.Append($returnNull)
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldloc, $result))
    $il.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))

    $handler = [Mono.Cecil.Cil.ExceptionHandler]::new([Mono.Cecil.Cil.ExceptionHandlerType]::Catch)
    $handler.CatchType = $exceptionType
    $handler.TryStart = $tryStart
    $handler.TryEnd = $catchStart
    $handler.HandlerStart = $catchStart
    $handler.HandlerEnd = $returnNull
    $body.ExceptionHandlers.Add($handler)

    # The installation-location controls are created lazily by ShowPage(1), not by
    # the form constructor. Patch immediately after pathBox receives its default.
    $ctor = $wizard.Methods | Where-Object Name -eq 'ShowPage' | Select-Object -First 1
    $pathField = $wizard.Fields | Where-Object Name -eq 'pathBox' | Select-Object -First 1
    if ($null -eq $ctor -or $null -eq $pathField) {
        throw 'Could not locate WizardForm ShowPage/pathBox field.'
    }

    $pathStore = $ctor.Body.Instructions |
        Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Stfld -and $_.Operand.Name -eq 'pathBox' } |
        Select-Object -First 1
    if ($null -eq $pathStore) { throw 'Could not locate pathBox initialization.' }
    $pathStoreIndex = $ctor.Body.Instructions.IndexOf($pathStore)
    $setText = $ctor.Body.Instructions[$pathStoreIndex - 2]
    if ($setText.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Callvirt -or $setText.Operand.FullName -notlike '*Control::set_Text(System.String)') {
        throw 'Could not locate pathBox default text setter.'
    }
    $previousLocal = [Mono.Cecil.Cil.VariableDefinition]::new($stringType)
    $ctor.Body.Variables.Add($previousLocal)
    $done = [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Nop)
    # Use explicit helper calls instead of the two-argument overloads. Newer
    # Mono.Cecil exposes several Create overloads that PowerShell cannot resolve
    # unambiguously for a MethodReference/VariableDefinition mix.
    $patch = New-Object System.Collections.Generic.List[Mono.Cecil.Cil.Instruction]
    $patch.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call, [Mono.Cecil.MethodReference]$previousMethod))
    $patch.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Stloc, [Mono.Cecil.Cil.VariableDefinition]$previousLocal))
    $patch.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldloc, [Mono.Cecil.Cil.VariableDefinition]$previousLocal))
    $patch.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Brfalse, [Mono.Cecil.Cil.Instruction]$done))
    $patch.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
    $patch.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldfld, [Mono.Cecil.FieldReference]$pathField))
    $patch.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldloc, [Mono.Cecil.Cil.VariableDefinition]$previousLocal))
    $patch.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Callvirt, [Mono.Cecil.MethodReference]$setText.Operand))
    $patch.Add($done)
    foreach ($instruction in $patch) { $ctor.Body.GetILProcessor().InsertAfter($pathStore, $instruction); $pathStore = $instruction }
}
