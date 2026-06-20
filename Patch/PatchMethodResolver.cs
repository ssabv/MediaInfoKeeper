using System;
using System.Linq;
using System.Reflection;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 定义目标方法名称、参数和返回值等精确签名条件。
    /// </summary>
    public sealed class MethodSignatureProfile
    {
        public string Name { get; set; }

        public string MethodName { get; set; }

        public BindingFlags BindingFlags { get; set; } =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public Type[] ParameterTypes { get; set; } = Type.EmptyTypes;

        public Type ReturnType { get; set; }

        public bool? IsStatic { get; set; }
    }

    /// <summary>
    /// 定义目标构造函数的精确签名条件。
    /// </summary>
    public sealed class ConstructorSignatureProfile
    {
        public string Name { get; set; }

        public BindingFlags BindingFlags { get; set; } = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public Type[] ParameterTypes { get; set; } = Type.EmptyTypes;
    }

    /// <summary>
    /// 根据精确签名解析补丁目标方法，并记录命中或失败日志。
    /// </summary>
    public static class PatchMethodResolver
    {
        public static MethodInfo Resolve(
            Type type,
            Version versionForLog,
            MethodSignatureProfile exactProfile,
            ILogger logger,
            string context)
        {
            var method = TryResolve(type, versionForLog, exactProfile, "exact", logger, context);
            if (method != null)
            {
                return method;
            }

            PatchLog.ResolveFailed(
                logger,
                context ?? "MethodResolve",
                type?.FullName ?? "<null>",
                versionForLog?.ToString() ?? "<unknown>");
            return null;
        }

        public static ConstructorInfo ResolveConstructor(
            Type type,
            Version versionForLog,
            ConstructorSignatureProfile exactProfile,
            ILogger logger,
            string context)
        {
            if (type == null || !IsValidProfile(exactProfile))
            {
                PatchLog.ResolveFailed(
                    logger,
                    context ?? "ConstructorResolve",
                    type?.FullName ?? "<null>",
                    versionForLog?.ToString() ?? "<unknown>");
                return null;
            }

            var constructor = type.GetConstructor(
                exactProfile.BindingFlags,
                null,
                exactProfile.ParameterTypes,
                null);

            if (constructor == null)
            {
                PatchLog.ResolveFailed(
                    logger,
                    context ?? "ConstructorResolve",
                    type.FullName,
                    versionForLog?.ToString() ?? "<unknown>");
                return null;
            }

            PatchLog.ResolveHit(
                logger,
                context ?? "ConstructorResolve",
                "exact",
                exactProfile.Name ?? ".ctor",
                BuildSignature(constructor),
                versionForLog?.ToString() ?? "<unknown>");
            return constructor;
        }

        private static MethodInfo TryResolve(
            Type type,
            Version versionForLog,
            MethodSignatureProfile profile,
            string level,
            ILogger logger,
            string context)
        {
            if (type == null || !IsValidProfile(profile))
            {
                return null;
            }

            var method = ResolveSingle(type, profile);
            if (method == null)
            {
                return null;
            }

            PatchLog.ResolveHit(
                logger,
                context ?? "MethodResolve",
                level,
                profile.Name ?? profile.MethodName ?? "unknown",
                BuildSignature(method),
                versionForLog?.ToString() ?? "<unknown>");
            return method;
        }

        private static MethodInfo ResolveSingle(Type type, MethodSignatureProfile profile)
        {
            if (type == null || !IsValidProfile(profile))
            {
                return null;
            }

            var method = type.GetMethod(
                profile.MethodName,
                profile.BindingFlags,
                null,
                profile.ParameterTypes,
                null);

            if (method == null)
            {
                method = type
                    .GetMethods(profile.BindingFlags)
                    .FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, profile.MethodName, StringComparison.Ordinal) &&
                        IsMatch(candidate, profile));
            }

            return IsMatch(method, profile) ? method : null;
        }

        private static bool IsMatch(MethodInfo method, MethodSignatureProfile profile)
        {
            if (method == null || profile == null)
            {
                return false;
            }

            if (profile.IsStatic.HasValue && method.IsStatic != profile.IsStatic.Value)
            {
                return false;
            }

            if (profile.ReturnType != null && method.ReturnType != profile.ReturnType)
            {
                return false;
            }

            if (profile.ParameterTypes == null)
            {
                return false;
            }

            var parameters = method.GetParameters().Select(p => p.ParameterType).ToArray();
            if (parameters.Length != profile.ParameterTypes.Length)
            {
                return false;
            }

            for (var i = 0; i < parameters.Length; i++)
            {
                if (!IsParameterTypeMatch(parameters[i], profile.ParameterTypes[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsParameterTypeMatch(Type actualType, Type expectedType)
        {
            if (actualType == expectedType)
            {
                return true;
            }

            if (actualType == null || expectedType == null)
            {
                return false;
            }

            if (expectedType.IsGenericTypeDefinition &&
                actualType.IsGenericType &&
                actualType.GetGenericTypeDefinition() == expectedType)
            {
                return true;
            }

            return false;
        }

        private static bool IsValidProfile(MethodSignatureProfile profile)
        {
            return profile != null &&
                   !string.IsNullOrWhiteSpace(profile.MethodName) &&
                   profile.ParameterTypes != null;
        }

        private static bool IsValidProfile(ConstructorSignatureProfile profile)
        {
            return profile != null && profile.ParameterTypes != null;
        }

        private static string BuildSignature(MethodInfo method)
        {
            if (method == null)
            {
                return "<null>";
            }

            var parameters = string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name));
            return string.Format(
                "{0}.{1}({2}) -> {3}",
                method.DeclaringType?.FullName ?? "<unknown>",
                method.Name,
                parameters,
                method.ReturnType?.Name ?? "<void>");
        }

        private static string BuildSignature(ConstructorInfo constructor)
        {
            if (constructor == null)
            {
                return "<null>";
            }

            var parameters = string.Join(",", constructor.GetParameters().Select(p => p.ParameterType.Name));
            return string.Format(
                "{0}..ctor({1})",
                constructor.DeclaringType?.FullName ?? "<unknown>",
                parameters);
        }
    }
}
