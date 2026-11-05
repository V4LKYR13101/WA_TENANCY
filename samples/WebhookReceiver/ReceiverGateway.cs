using System.Net.Http.Json;
using System.Text.Json;

public sealed class ReceiverGateway(IConfiguration configuration) : IDisposable
{
    private readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect=false }) { Timeout=TimeSpan.FromSeconds(15) };
    public bool CanSend => !string.IsNullOrWhiteSpace(configuration["Receiver:ApiKey"]);
    public async Task Media(Guid tenant, Guid attachment, HttpContext context)
    {
        if (!CanSend) { context.Response.StatusCode=409; return; }
        try
        {
            using var meRequest=Request(HttpMethod.Get,"/api/v1/me");using var me=await client.SendAsync(meRequest,context.RequestAborted);
            if(!me.IsSuccessStatusCode) {context.Response.StatusCode=502;return;}
            using var identity=JsonDocument.Parse(await me.Content.ReadAsStringAsync(context.RequestAborted));
            if(identity.RootElement.GetProperty("tenantId").GetGuid()!=tenant) {context.Response.StatusCode=403;return;}
            using var request=Request(HttpMethod.Get,$"/api/v1/media/{attachment}");
            if(context.Request.Headers.Range.ToString() is {Length:>0 and <200} range) request.Headers.TryAddWithoutValidation("Range",range);
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,context.RequestAborted);
            context.Response.StatusCode=(int)response.StatusCode;
            if(!response.IsSuccessStatusCode)return;
            context.Response.ContentType=response.Content.Headers.ContentType?.ToString()??"application/octet-stream";
            context.Response.ContentLength=response.Content.Headers.ContentLength;
            if(response.Content.Headers.ContentRange is {} contentRange)context.Response.Headers.ContentRange=contentRange.ToString();
            context.Response.Headers.AcceptRanges="bytes";
            await response.Content.CopyToAsync(context.Response.Body,context.RequestAborted);
        }
        catch(HttpRequestException) {if(!context.Response.HasStarted)context.Response.StatusCode=502;}
        catch(OperationCanceledException) when(!context.RequestAborted.IsCancellationRequested) {if(!context.Response.HasStarted)context.Response.StatusCode=504;}
    }
    private HttpRequestMessage Request(HttpMethod method,string path)
    {
        var root=configuration["Receiver:ServiceBaseUrl"] ?? "http://127.0.0.1:5187";
        var request=new HttpRequestMessage(method,root.TrimEnd('/')+path); request.Headers.Add("X-Api-Key",configuration["Receiver:ApiKey"]); return request;
    }
    public async Task<(int Status,string Body)> Reply(Guid tenant,Guid account,string chat,string text,string requestId,CancellationToken ct)
    {
        if (!CanSend) return (409,"Configure Receiver:ApiKey in User Secrets to enable replies.");
        try
        {
            using var meRequest=Request(HttpMethod.Get,"/api/v1/me"); using var me=await client.SendAsync(meRequest,ct);
            if (!me.IsSuccessStatusCode) return (502,"The service rejected the receiver API key.");
            using var identity=JsonDocument.Parse(await me.Content.ReadAsStringAsync(ct));
            if (identity.RootElement.GetProperty("tenantId").GetGuid()!=tenant) return (403,"This conversation belongs to a different company than the configured API key.");
            using var request=Request(HttpMethod.Post,"/api/v1/messages"); request.Headers.Add("Idempotency-Key",requestId);
            request.Content=JsonContent.Create(new { accountId=account,recipient=chat,text });
            using var response=await client.SendAsync(request,ct); var body=await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode==System.Net.HttpStatusCode.Accepted) return (202,body);
            var message="The service rejected the reply.";
            try { using var json=JsonDocument.Parse(body); message=InboxStore.Text(json.RootElement,"title") is { Length:>0 } title ? title : message; } catch(JsonException) { }
            return ((int)response.StatusCode,message);
        }
        catch(HttpRequestException) { return (502,"The WhatsApp service is unavailable. Retry with the same request ID."); }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested) { return (504,"The service request timed out. Retry with the same request ID."); }
    }
    public async Task Refresh(InboxStore store,CancellationToken ct)
    {
        if (!CanSend || !await store.HasActiveReplies()) return;
        try { using var request=Request(HttpMethod.Get,"/api/v1/jobs"); using var response=await client.SendAsync(request,ct); if(response.IsSuccessStatusCode) { using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); await store.UpdateReplies(json.RootElement); } }
        catch(HttpRequestException) { } catch(JsonException) { } catch(OperationCanceledException) when(!ct.IsCancellationRequested) { }
    }
    public async Task<(int Status,string Body)> Retry(Guid tenant,Guid job,CancellationToken ct)
    {
        if(!CanSend)return(409,"Configure Receiver:ApiKey to retry replies.");
        try
        {
            using var identityRequest=Request(HttpMethod.Get,"/api/v1/me");using var identityResponse=await client.SendAsync(identityRequest,ct);
            if(!identityResponse.IsSuccessStatusCode)return(502,"The service rejected the receiver API key.");
            using var identity=JsonDocument.Parse(await identityResponse.Content.ReadAsStringAsync(ct));
            if(identity.RootElement.GetProperty("tenantId").GetGuid()!=tenant)return(403,"The API key belongs to a different company.");
            using var request=Request(HttpMethod.Post,$"/api/v1/jobs/{job}/retry");using var response=await client.SendAsync(request,ct);var body=await response.Content.ReadAsStringAsync(ct);
            if(response.StatusCode==System.Net.HttpStatusCode.Accepted)return(202,body);
            try{using var json=JsonDocument.Parse(body);return((int)response.StatusCode,InboxStore.Text(json.RootElement,"title"));}catch(JsonException){return((int)response.StatusCode,"The service could not retry this message.");}
        }
        catch(HttpRequestException){return(502,"The WhatsApp service is unavailable.");}
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){return(504,"The retry request timed out. Refresh the message status before retrying again.");}
    }
    public void Dispose()=>client.Dispose();
}
