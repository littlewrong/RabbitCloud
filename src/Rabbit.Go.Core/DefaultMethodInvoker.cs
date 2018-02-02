﻿using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Rabbit.Go.Abstractions;
using Rabbit.Go.Abstractions.Codec;
using Rabbit.Go.Codec;
using Rabbit.Go.Core.Internal;
using Rabbit.Go.Formatters;
using Rabbit.Go.Internal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Rabbit.Go.Core
{
    public class DefaultMethodInvoker : InterceptorMethodInvoker
    {
        private readonly ICodec _codec;
        private readonly IKeyValueFormatterFactory _keyValueFormatterFactory;
        private readonly ITemplateParser _templateParser;
        private readonly IGoClient _client;

        public DefaultMethodInvoker(RequestContext requestContext, MethodInvokerEntry entry)
            : base(requestContext, entry.Interceptors)
        {
            _client = entry.Client;
            _codec = entry.Codec;
            _keyValueFormatterFactory = entry.KeyValueFormatterFactory;
            _templateParser = entry.TemplateParser;
        }

        #region Overrides of InterceptorMethodInvoker

        protected override async Task<object> DoInvokeAsync(object[] arguments)
        {
            var goContext = RequestContext.GoContext;
            await InitializeRequestAsync(goContext.Request, arguments);

            await _client.RequestAsync(goContext);

            return await DecodeAsync(goContext.Response);
        }

        #endregion Overrides of InterceptorMethodInvoker

        private static void BuildQueryAndHeaders(GoRequest request, IDictionary<ParameterTarget, IDictionary<string, StringValues>> parameters)
        {
            if (parameters == null)
                return;
            foreach (var item in parameters)
            {
                var target = item.Value;
                Func<string, StringValues, GoRequest> set;
                switch (item.Key)
                {
                    case ParameterTarget.Query:
                        set = request.AddQuery;
                        break;

                    case ParameterTarget.Header:
                        set = request.AddHeader;
                        break;

                    default:
                        continue;
                }

                foreach (var t in target)
                {
                    set(t.Key, t.Value);
                }
            }
        }

        private static async Task BuildBodyAsync(GoRequest request, IEncoder encoder, IReadOnlyList<ParameterDescriptor> parameterDescriptors, object[] arguments)
        {
            if (encoder == null)
                return;

            object bodyArgument = null;
            Type bodyType = null;
            for (var i = 0; i < parameterDescriptors.Count; i++)
            {
                var parameterDescriptor = parameterDescriptors[i];
                if (parameterDescriptor.Target != ParameterTarget.Body)
                    continue;

                bodyArgument = arguments[i];
                bodyType = parameterDescriptor.ParameterType;
                break;
            }

            if (bodyArgument == null || bodyType == null)
                return;

            try
            {
                await encoder.EncodeAsync(bodyArgument, bodyType, request);
            }
            catch (EncodeException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new EncodeException(e.Message, e);
            }
        }

        private static async Task<IDictionary<ParameterTarget, IDictionary<string, StringValues>>> FormatAsync(IReadOnlyList<ParameterDescriptor> parameterDescriptors, IKeyValueFormatterFactory keyValueFormatterFactory, IReadOnlyList<object> arguments)
        {
            if (keyValueFormatterFactory == null)
                return null;

            IDictionary<ParameterTarget, IDictionary<string, StringValues>> formatResult =
                new Dictionary<ParameterTarget, IDictionary<string, StringValues>>
                {
                    {
                        ParameterTarget.Query,
                        new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
                    },
                    {ParameterTarget.Path, new Dictionary<string, StringValues>()},
                    {
                        ParameterTarget.Header,
                        new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
                    }
                };

            for (var i = 0; i < parameterDescriptors.Count; i++)
            {
                var parameterDescriptor = parameterDescriptors[i];

                if (!formatResult.TryGetValue(parameterDescriptor.Target, out var itemResult))
                    continue;

                var parameter = parameterDescriptors[i];
                var value = arguments[i];
                var item = await keyValueFormatterFactory.FormatAsync(value, parameter.ParameterType, parameterDescriptor.Name);

                foreach (var t in item)
                    itemResult[t.Key] = t.Value;
            }

            return formatResult;
        }

        private async Task<object> DecodeAsync(GoResponse response)
        {
            try
            {
                return _codec?.Decoder == null
                    ? null
                    : await _codec?.Decoder.DecodeAsync(response, RequestContext.MethodDescriptor.ReturnType);
            }
            catch (DecodeException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new DecodeException(e.Message, e);
            }
        }

        private async Task InitializeRequestAsync(GoRequest request, object[] arguments)
        {
            var methodDescriptor = RequestContext.MethodDescriptor;

            var formatResult = await FormatAsync(methodDescriptor.Parameters, _keyValueFormatterFactory, arguments);

            var urlTemplate = methodDescriptor.UrlTemplate;

            var url = urlTemplate.Template;
            if (urlTemplate.NeedParse)
                url = _templateParser.Parse(urlTemplate.Template, formatResult[ParameterTarget.Path].ToDictionary(i => i.Key, i => i.Value.ToString()));

            var uri = new Uri(url);

            request.Scheme = uri.Scheme;
            request.Host = uri.Host;
            request.Port = uri.Port;
            var pathAndQuery = uri.PathAndQuery;

            var queryStartIndex = pathAndQuery.IndexOf('?');

            if (queryStartIndex == -1)
            {
                request.Path = pathAndQuery;
            }
            else
            {
                request.Path = pathAndQuery.Substring(0, queryStartIndex);
                var queryString = pathAndQuery.Substring(queryStartIndex);
                var query = QueryHelpers.ParseNullableQuery(queryString);
                if (query != null && query.Any())
                {
                    foreach (var item in query)
                        request.AddQuery(item.Key, item.Value);
                }
            }

            await BuildBodyAsync(request, _codec.Encoder, methodDescriptor.Parameters, arguments);
            BuildQueryAndHeaders(request, formatResult);
            //todo:考虑 httpmethod 是否直接强类型定义
            //            RequestContext.GoContext.Items["HttpMethod"] = GetHttpMethod(methodDescriptor.Method, HttpMethod.Get);
        }
    }
}