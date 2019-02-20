﻿#region Copyright 2018 D-Haven.org
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TypeInfo = System.Reflection.TypeInfo;

namespace DHaven.Faux.Compiler
{
    public class CoreWebServiceClassGenerator : IWebServiceClassGenerator
    {
        public CompilerConfig Config { get; }

        private readonly ILogger logger;
        
        public CoreWebServiceClassGenerator(IOptions<CompilerConfig> options, ILogger<CoreWebServiceClassGenerator> logger)
        {
            // This is temporary until I get the FauxServiceProvider working for options.
            Config = options?.Value ?? new CompilerConfig();
            this.logger = logger;

            if (string.IsNullOrEmpty(Config.RootNamespace))
            {
                Config.RootNamespace = "DHaven.Feign.Wrapper";
            }

            if (string.IsNullOrEmpty(Config.SourceFilePath))
            {
                Config.SourceFilePath = "./dhaven-faux";
            }

            if (!Directory.Exists(Config.SourceFilePath))
            {
                Directory.CreateDirectory(Config.SourceFilePath);
            }
        }

        public IEnumerable<string> GenerateSource(TypeInfo typeInfo, out string fullClassName)
        {
            if (!typeInfo.IsInterface || !typeInfo.IsPublic)
            {
                throw new ArgumentException($"{typeInfo.FullName} must be a public interface");
            }

            if (typeInfo.IsGenericType)
            {
                throw new NotSupportedException($"Generic interfaces are not supported: {typeInfo.FullName}");
            }

            var className = typeInfo.FullName?.Replace(".", string.Empty);
            fullClassName = $"{Config.RootNamespace}.{className}";

            using (logger.BeginScope("Generator {0}:", className))
            {
                var serviceName = typeInfo.GetCustomAttribute<FauxClientAttribute>().Name;
                var baseRoute = typeInfo.GetCustomAttribute<FauxClientAttribute>().Route ?? string.Empty;
                var sealedString = Config.GenerateSealedClasses ? "sealed" : string.Empty;

                logger.LogTrace("Beginning to generate source");

                using (var namespaceBuilder = new IndentBuilder())
                {
                    namespaceBuilder.AppendLine($"namespace {Config.RootNamespace}");
                    namespaceBuilder.AppendLine("{");
                    using (var classBuilder = namespaceBuilder.Indent())
                    {
                        classBuilder.AppendLine("// Generated by DHaven.Faux");
                        classBuilder.AppendLine(
                            $"public {sealedString} class {className} : DHaven.Faux.HttpSupport.DiscoveryAwareBase, {typeInfo.FullName}");
                        classBuilder.AppendLine("{");

                        using (var fieldBuilder = classBuilder.Indent())
                        {
                            fieldBuilder.AppendLine("private readonly Microsoft.Extensions.Logging.ILogger 仮logger;");
                        }

                        using (var constructorBuilder = classBuilder.Indent())
                        {
                            constructorBuilder.AppendLine($"public {className}(DHaven.Faux.HttpSupport.IHttpClient client,");
                            constructorBuilder.AppendLine("        Microsoft.Extensions.Logging.ILogger logger)");
                            constructorBuilder.AppendLine($"    : base(client, \"{serviceName}\", \"{baseRoute}\")");
                            constructorBuilder.AppendLine("{");
                            using (var insideCxrBuilder = constructorBuilder.Indent())
                            {
                                insideCxrBuilder.AppendLine("仮logger = logger;");
                            }

                            constructorBuilder.AppendLine("}");
                        }

                        foreach (var method in typeInfo.GetMethods())
                        {
                            using (var methodBuilder = classBuilder.Indent())
                            {
                                BuildMethod(methodBuilder, method);
                            }
                        }

                        classBuilder.AppendLine("}");
                    }

                    namespaceBuilder.AppendLine("}");

                    var sourceCode = namespaceBuilder.ToString();

                    logger.LogTrace("Source generated");

                    if (Config.OutputSourceFiles)
                    {
                        var fullPath = Path.Combine(Config.SourceFilePath, $"{className}.cs");
                        try
                        {
                            logger.LogTrace("Writing source file: {0}", fullPath);
                            File.WriteAllText(fullPath, sourceCode, Encoding.UTF8);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Could not write the source code for {0}", fullPath);
                        }
                    }

                    return new[] {sourceCode};
                }
            }
        }
 
        private void BuildMethod(IndentBuilder classBuilder, MethodInfo method)
        {
            var isAsyncCall = typeof(Task).IsAssignableFrom(method.ReturnType);
            var returnType = method.ReturnType;

            if(isAsyncCall && method.ReturnType.IsConstructedGenericType)
            {
                returnType = method.ReturnType.GetGenericArguments()[0];
            }

            var isVoid = returnType == typeof(void) || (isAsyncCall && !method.ReturnType.IsConstructedGenericType);

            // Write the method declaration

            classBuilder.Append("public ");
            if (isAsyncCall)
            {
                classBuilder.Append("async ");
                classBuilder.Append(typeof(Task).FullName);

                if(!isVoid)
                {
                    classBuilder.Append($"<{CompilerUtils.ToCompilableName(returnType)}>");
                }
            }
            else
            {
                classBuilder.Append(isVoid ? "void" : CompilerUtils.ToCompilableName(returnType));
            }

            var attribute = method.GetCustomAttribute<HttpMethodAttribute>();

            classBuilder.Append($" {method.Name}(");
            classBuilder.Append(string.Join(", ", method.GetParameters().Select(CompilerUtils.ToParameterDeclaration)));
            classBuilder.AppendLine(")");
            classBuilder.AppendLine("{");

            using (var methodBuilder = classBuilder.Indent())
            {
                methodBuilder.AppendLine(
                    "var 仮variables = new System.Collections.Generic.Dictionary<string,object>();");
                methodBuilder.AppendLine(
                    "var 仮reqParams = new System.Collections.Generic.Dictionary<string,string>();");

                var contentHeaders = new Dictionary<string, ParameterInfo>();
                var requestHeaders = new Dictionary<string, ParameterInfo>();
                var responseHeaders = new Dictionary<string, ParameterInfo>();
                ParameterInfo bodyParam = null;
                BodyAttribute bodyAttr = null;

                foreach (var parameter in method.GetParameters())
                {
                    AttributeInterpreter.InterpretPathValue(parameter, methodBuilder);
                    AttributeInterpreter.InterpretRequestHeader(parameter, requestHeaders, contentHeaders);
                    AttributeInterpreter.InterpretBodyParameter(parameter, ref bodyParam, ref bodyAttr);
                    AttributeInterpreter.InterpretRequestParameter(parameter, methodBuilder);
                    AttributeInterpreter.InterpretResponseHeaderInParameters(parameter, isAsyncCall,
                        ref responseHeaders);
                }

                methodBuilder.AppendLine(
                    $"var 仮request = CreateRequest({CompilerUtils.ToCompilableName(attribute.Method)}, \"{attribute.Path}\", 仮variables, 仮reqParams);");
                var hasContent = AttributeInterpreter.CreateContentObjectIfSpecified(bodyAttr, bodyParam, methodBuilder);

                foreach (var entry in requestHeaders)
                {
                    methodBuilder.AppendLine(
                        $"仮request.Headers.Add(\"{entry.Key}\", {entry.Value.Name}{(entry.Value.ParameterType.IsClass ? "?" : "")}.ToString());");
                }

                if (hasContent)
                {
                    // when setting content we can apply the contentHeaders
                    foreach (var entry in contentHeaders)
                    {
                        methodBuilder.AppendLine(
                            $"仮content.Headers.Add(\"{entry.Key}\", {entry.Value.Name}{(entry.Value.ParameterType.IsClass ? "?" : "")}.ToString());");
                    }

                    methodBuilder.AppendLine("仮request.Content = 仮content;");
                }

                methodBuilder.AppendLine(isAsyncCall
                    ? "var 仮response = await InvokeAsync(仮request);"
                    : "var 仮response = Invoke(仮request);");

                foreach (var entry in responseHeaders)
                {
                    methodBuilder.AppendLine(
                        $"{entry.Value.Name} = GetHeaderValue<{CompilerUtils.ToCompilableName(entry.Value.ParameterType)}>(仮response, \"{entry.Key}\");");
                }

                if (!isVoid)
                {
                    var returnBodyAttribute = method.ReturnParameter?.GetCustomAttribute<BodyAttribute>();
                    var returnResponseAttribute = method.ReturnParameter?.GetCustomAttribute<ResponseHeaderAttribute>();

                    if (returnResponseAttribute != null && returnBodyAttribute != null)
                    {
                        throw new WebServiceCompileException(
                            $"Cannot have different types of response attributes.  You had [{string.Join(", ", "Body", "ResponseHeader")}]");
                    }

                    if (returnResponseAttribute != null)
                    {
                        AttributeInterpreter.ReturnResponseHeader(returnResponseAttribute, returnType, methodBuilder);
                    }
                    else
                    {
                        if (returnBodyAttribute == null)
                        {
                            returnBodyAttribute = new BodyAttribute();
                        }

                        AttributeInterpreter.ReturnContentObject(returnBodyAttribute, returnType, isAsyncCall,
                            methodBuilder);
                    }
                }
            }

            classBuilder.AppendLine("}");
        }   
    }
}