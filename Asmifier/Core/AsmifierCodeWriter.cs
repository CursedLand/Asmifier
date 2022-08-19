using System.Text;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.DotNet.Signatures.Types;
using AsmResolver.PE.DotNet.Cil;

namespace Asmifier.Core; 

public class AsmifierCodeWriter {

    private ModuleDefinition _module;

    private string?[] _primitives = typeof(string).Module.GetTypes().Where(t => t.IsPrimitive).Select(t => t.FullName)
        .Concat(new[] { "System.String", "System.Object" }).ToArray();
    private StringBuilder _builder = new();
    private Dictionary<IMemberDefinition, string> _prefixes = new();
    private Dictionary<string, int> _duplicatedIdentifiers = new();
    private Dictionary<CilInstruction, string> _labelsData = new();
    private Dictionary<CilLocalVariable, string> _variablesData = new();
    
    public AsmifierCodeWriter(ModuleDefinition module) {
        BuildModule(_module = module);
    }

    private void BuildModule(ModuleDefinition module) {
        
        BuildTypes(module.GetAllTypes());
    }
    
    private void BuildTypes(IEnumerable<TypeDefinition> types) {
        foreach (var type in types) {
            BuildType(type);
        }
    }
    
    private void BuildType(TypeDefinition type) {

        var typePrefix = BuildPrefix(type);

        BuildVariable(typePrefix, BuildNewObj(nameof(TypeDefinition), type.Namespace.Quote(), type.Name.Quote(), BuildAttributes(type.Attributes)));

        foreach (var method in type.Methods) {
            BuildMethod(method);
        }
    }

    private void BuildMethod(MethodDefinition method) {

        var methodPrefix = BuildPrefix(method);
        
        BuildVariable(methodPrefix, BuildNewObj(nameof(MethodDefinition), method.Name.Quote(), BuildAttributes(method.Attributes), BuildMethodSignature(method, method.Signature!)));

        if (method.CilMethodBody is not null) {
            BuildMethodBody(method.CilMethodBody);   
        }
    }

    private string BuildMethodSignature(MethodDefinition method, MethodSignature signature) {

        var ret = BuildTypeSignature(signature.ReturnType);
        var parameters = string.Join(", ", signature.ParameterTypes.Select(t => BuildTypeSignature(t)));
        
        return method.IsStatic
            ? $"{nameof(MethodSignature)}.{nameof(MethodSignature.CreateStatic)}({ret}{(!string.IsNullOrEmpty(parameters) ? $", {parameters}" : "")})"
            : $"{nameof(MethodSignature)}.{nameof(MethodSignature.CreateInstance)}({ret}{(!string.IsNullOrEmpty(parameters) ? $", {parameters}" : "")})";
    }

    private void BuildMethodBody(CilMethodBody body) {
        
        var methodPrefix = BuildPrefix(body.Owner);

        BuildSetter(methodPrefix, BuildNewObj(nameof(CilMethodBody), methodPrefix), nameof(MethodDefinition.CilMethodBody));

        // VerifyLabelsOnBuild or ComputeMaxStackOnBuild or both.
        // BuildSetter(methodPrefix, BuildAttributes(body.BuildFlags), nameof(MethodDefinition.CilMethodBody), nameof(CilMethodBody.BuildFlags));

        if (body.InitializeLocals) BuildSetter(methodPrefix, body.InitializeLocals.FormatBool(), nameof(MethodDefinition.CilMethodBody), nameof(CilMethodBody.InitializeLocals));

        if (!body.VerifyLabelsOnBuild) BuildSetter(methodPrefix, body.VerifyLabelsOnBuild.FormatBool(), nameof(MethodDefinition.CilMethodBody), nameof(CilMethodBody.VerifyLabelsOnBuild));
        
        if (!body.ComputeMaxStackOnBuild) BuildSetter(methodPrefix, body.ComputeMaxStackOnBuild.FormatBool(), nameof(MethodDefinition.CilMethodBody), nameof(CilMethodBody.ComputeMaxStackOnBuild));
        
        // Max stack getting calculated in writing process.

        _builder.AppendLine();

        if (body.Instructions.Count is not 0) {

            var instructionsPrefix = $"{methodPrefix}_cil";
            
            BuildVariable(instructionsPrefix, BuildGetter(methodPrefix, nameof(MethodDefinition.CilMethodBody), nameof(CilMethodBody.Instructions)));

            BuildVariables(body);
            BuildBranches(body);

            foreach (var instruction in body.Instructions) {
                BuildInstruction(instruction, instructionsPrefix);
            }

            _builder.AppendLine();
        }
        
    }

    private void BuildVariables(CilMethodBody body) {
        var methodPrefix = BuildPrefix(body.Owner);

        foreach (var variable in body.LocalVariables) {
            var variablePrefix = $"{methodPrefix}_V_{variable.Index}";
            BuildVariable(variablePrefix, BuildNewObj(nameof(CilLocalVariable)), false);
            _variablesData[variable] = variablePrefix;
            var expression = $"{methodPrefix}.{nameof(MethodDefinition.CilMethodBody)}.{nameof(CilMethodBody.LocalVariables)}.Add({variablePrefix});";
            _builder.AppendLine(expression);
        }
        if (body.LocalVariables.Count != 0) _builder.AppendLine();
        
    }

    private void BuildBranches(CilMethodBody body) {

        var methodPrefix = BuildPrefix(body.Owner);

        var branches = body.Instructions.Where(i => i.Operand is ICilLabel)
            .Select(i => (ICilLabel)i.Operand!)
            .Concat(body.Instructions.Where(i => i.Operand is IList<ICilLabel>)
            .Select(i => (IList<ICilLabel>)i.Operand!).SelectMany(i => i.ToArray()))
            .Select(i => (CilInstructionLabel)i)
            .Distinct()
            .ToArray();

        foreach (var branch in branches) {
            var branchPrefix = $"{methodPrefix}_{branch.Offset:x4}";
            BuildVariable(branchPrefix, BuildNewObj(nameof(CilInstructionLabel)), false);
            _labelsData[branch.Instruction!] = branchPrefix;
        }

        if (branches.Length != 0) _builder.AppendLine();
    }

    private void BuildInstruction(CilInstruction instruction, string prefix) {

        var operandExpression = BuildOperand(instruction);

        var expression = $"{prefix}.Add({nameof(CilOpCodes)}.{instruction.OpCode.Code.ToString()}{(operandExpression is null ? string.Empty : $", {operandExpression}")})";
        
        if (_labelsData.TryGetValue(instruction, out var branchPrefix)) {
            BuildSetter(branchPrefix, expression,nameof(CilInstructionLabel.Instruction));
            return;
        }

        _builder.AppendLine($"{expression};"); // :-)
    }

    private string? BuildOperand(CilInstruction instruction) {
        switch (instruction.OpCode.OperandType) {
            
            case CilOperandType.ShortInlineBrTarget:
            case CilOperandType.InlineBrTarget:
                var targetInstruction = ((CilInstructionLabel)instruction.Operand!).Instruction!;
                return _labelsData[targetInstruction];

            case CilOperandType.InlineI:
            case CilOperandType.InlineI8:
            case CilOperandType.InlineR:
            case CilOperandType.ShortInlineI:
            case CilOperandType.ShortInlineR:
                var convertible = instruction.Operand as IConvertible;
                if (convertible is null) throw new Exception("Invalid operand.");
                // ReSharper disable once SpecifyACultureInStringConversionExplicitly
                return convertible.ToString();
            
            // ????????????????????????????
            case CilOperandType.InlineField:
            case CilOperandType.InlineMethod:
            case CilOperandType.InlineTok:
            case CilOperandType.InlineType:
                return "/* not supported yet :-) */ ";
            
            case CilOperandType.InlineNone:
                return null;
            
            // ????????????????????????????
            case CilOperandType.InlinePhi:
            case CilOperandType.InlineSig:
                break;
            
            case CilOperandType.InlineString:
                return ((string?)instruction.Operand).Quote();
            
            case CilOperandType.InlineSwitch:
                return string.Join(", ", ((IList<ICilLabel>)instruction.Operand!).Select(i => (CilInstructionLabel)i) .Select(i => _labelsData[i.Instruction!]));
            
            case CilOperandType.InlineVar:
            case CilOperandType.ShortInlineVar:
                var variablePrefix = instruction.Operand as CilLocalVariable;
                if (variablePrefix is null) throw new Exception("Invalid Operand.");
                return _variablesData[variablePrefix];
            
            // ????????????????????????????
            case CilOperandType.InlineArgument:
            case CilOperandType.ShortInlineArgument:
                break;

            default:
                throw new ArgumentOutOfRangeException();
        }
        return null;
    }

    private string BuildTypeSignature(TypeSignature signature) {

        var isCorlib = () => {
            if (signature.GetUnderlyingTypeDefOrRef() is { } type) {
                return _primitives.Contains(type.FullName);
            }

            return false;
        };

        _module.CorLibTypeFactory.Boolean.MakeSzArrayType();
        return signature switch {
            CorLibTypeSignature corLib => $"module.CorLibTypeFactory.{corLib.Name}", /* System.String, System.Int32, System.Int64, etc. */ 
            // BoxedTypeSignature boxed => throw new NotImplementedException(),
            ByReferenceTypeSignature byReference => $"{(isCorlib() ? $"module.CorLibTypeFactory.{byReference.Name}" : "")}.MakeByReferenceType()",
            // ArrayTypeSignature array => throw new NotImplementedException(),
            // CustomModifierTypeSignature customModifier => throw new NotImplementedException(),
            // FunctionPointerTypeSignature functionPointer => throw new NotImplementedException(),
            // GenericInstanceTypeSignature genericInstance => throw new NotImplementedException(),
            // GenericParameterSignature genericParameterSignature => throw new NotImplementedException(),
            // PinnedTypeSignature pinned => throw new NotImplementedException(),
            // PointerTypeSignature pointer => throw new NotImplementedException(),
            // SentinelTypeSignature sentinel => throw new NotImplementedException(),
            SzArrayTypeSignature szArray => $"{(isCorlib() ? $"module.CorLibTypeFactory.{szArray.GetUnderlyingTypeDefOrRef()!.Name}" : "")}.MakeSzArrayType()",
            // TypeDefOrRefSignature typeDefOrRefSignature => throw new NotImplementedException(),
            // TypeSpecificationSignature typeSpecificationSignature => throw new NotImplementedException(),
            _ => null!
        };
    }
    
    private void BuildVariable(string name, string value, bool newLine = true) {
        _builder.AppendLine($"var {name} = {value};");
        if (newLine) _builder.AppendLine();
    }

    private void BuildSetter(string prefix, string value, params string[] properties) => _builder.AppendLine($"{prefix}.{string.Join(".", properties)} = {value};");

    private string BuildGetter(string prefix, params string[] properties) => $"{prefix}.{string.Join(".", properties)}";
    
    private string BuildNewObj(string ctor, params object[] args) => $"new {ctor}({string.Join(", ", args)})";

    private string BuildAttributes<TEnum>(TEnum attributes) where TEnum : struct, Enum {
        // TODO: optimize?
        var enumValues = Enum.GetValues<TEnum>().Where(value => attributes.HasFlag(value)).Select(value => $"{typeof(TEnum).Name}.{value}").Distinct();
        return string.Join(" | ", enumValues);
    }

    private string BuildPrefix(IMemberDefinition member) {
        if (_prefixes.TryGetValue(member, out var prefix))
            return prefix;

        var generatedPrefix = member switch {
            MethodDefinition method => $"m_{method.Name.AsUsableString()}",
            TypeDefinition type => $"t_{type.Name.AsUsableString()}",
            FieldDefinition field => $"f_{field.Name.AsUsableString()}",
            EventDefinition @event => $"e_{@event.Name.AsUsableString()}",
            PropertyDefinition property => $"e_{property.Name.AsUsableString()}",
            _ => throw new ArgumentOutOfRangeException(nameof(member), member, "Unexpected member type.")
        };

        var additionalPrefix = new int?();

        if (_prefixes.ContainsValue(generatedPrefix)) {
            additionalPrefix = _duplicatedIdentifiers.ContainsKey(generatedPrefix)
                ? ++_duplicatedIdentifiers[generatedPrefix]
                : _duplicatedIdentifiers[generatedPrefix] = 1;
        }

        if (additionalPrefix.HasValue) {
            generatedPrefix = $"{generatedPrefix}_{additionalPrefix}";
        }

        _prefixes.Add(member, generatedPrefix);

        return generatedPrefix;
    }
    
    public string GetResult() => _builder.ToString();
}