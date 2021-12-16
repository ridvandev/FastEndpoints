﻿using FastEndpoints.Validation;
using System.Diagnostics;

namespace FastEndpoints;

internal sealed class EndpointData
{
    //using Lazy<T> to prevent contention when WAF testing (see issue #10)
    private readonly Lazy<EndpointDefinition[]> _endpoints = new(() =>
    {
        Watch.Start();
        var epDefs = GenerateEndpointDefinitions();
        Watch.Stop();

        if (epDefs is null || epDefs.Length == 0)
            throw new InvalidOperationException("FastEndpoints was unable to discover any endpoint declarations!");

        return epDefs;
    });

    internal EndpointDefinition[] Definitions => _endpoints.Value;

    internal static Stopwatch Watch { get; } = new();

    private static EndpointDefinition[]? GenerateEndpointDefinitions()
    {

        var excludes = new[]
        {
                "Microsoft.",
                "System.",
                "FastEndpoints.",
                "testhost",
                "netstandard",
                "Newtonsoft.",
                "mscorlib",
                "NuGet."
            };

        var discoveredTypes = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a =>
                !a.IsDynamic &&
                !excludes.Any(n => a.FullName!.StartsWith(n)))
            .SelectMany(a => a.GetTypes())
            .Where(t =>
                !t.IsAbstract &&
                !t.IsInterface &&
                t.GetInterfaces().Intersect(new[] {
                        typeof(IEndpoint),
                        typeof(IValidator),
                        typeof(IEventHandler)
                }).Any());

        if (!discoveredTypes.Any())
            throw new InvalidOperationException("Unable to find any endpoint declarations!");

        //Endpoint<TRequest>
        //Validator<TRequest>

        var epList = new List<(Type tEndpoint, Type tRequest)>();

        //key: TRequest //val: TValidator
        var valDict = new Dictionary<Type, Type>();

        foreach (var type in discoveredTypes)
        {
            var interfacesOfType = type.GetInterfaces();

            if (interfacesOfType.Contains(typeof(IEventHandler)))
            {
                ((IEventHandler?)Activator.CreateInstance(type))?.Subscribe();
                continue;
            }
            if (interfacesOfType.Contains(typeof(IEndpoint)))
            {
                var tRequest = typeof(EmptyRequest);

                if (type.BaseType?.IsGenericType is true)
                    tRequest = type.BaseType?.GetGenericArguments()?[0] ?? tRequest;

                epList.Add((type, tRequest));
            }
            else
            {
                Type tRequest = type.BaseType?.GetGenericArguments()[0]!;
                valDict.Add(tRequest, type);
            }
        }

        return epList
            .Select(x =>
            {
                var instance = (IEndpoint)Activator.CreateInstance(x.tEndpoint)!;
                instance?.Configure();
                return new EndpointDefinition()
                {
                    EndpointType = x.tEndpoint,
                    ValidatorType = valDict.GetValueOrDefault(x.tRequest),
                    Settings = (EndpointSettings)BaseEndpoint.SettingsPropInfo.GetValue(instance)!
                };
            })
            .ToArray();
    }
}

