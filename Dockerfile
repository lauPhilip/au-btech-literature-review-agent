# TraceableAI in a container. Build: docker build -t traceableai .
# Run (Mistral):  docker run -p 8080:8080 -e MISTRAL_API_KEY=... -e OpenSources__ContactEmail=you@uni.dk \
#                   -v traceable-runs:/app/WorkspaceStore -v traceable-data:/app/App_Data traceableai
# Run with a local Ollama on the host instead of Mistral:
#   docker run -p 8080:8080 -e Llm__Provider=OpenAICompatible -e Llm__Model=llama3.1 \
#     -e Llm__BaseUrl=http://host.docker.internal:11434/v1 --add-host=host.docker.internal:host-gateway traceableai
# Configuration keys use a double underscore for sections (Llm__Model = Llm:Model).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG APP_VERSION=0.0.0-local
WORKDIR /src
COPY AuBtechReviewAgent/AuBtechReviewAgent.csproj AuBtechReviewAgent/
RUN dotnet restore AuBtechReviewAgent/AuBtechReviewAgent.csproj
COPY AuBtechReviewAgent/ AuBtechReviewAgent/
RUN dotnet publish AuBtechReviewAgent/AuBtechReviewAgent.csproj -c Release -o /app/publish --no-restore \
      -p:InformationalVersion=${APP_VERSION#v}

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
# Runs, quota counters and the cache live in these folders; mount volumes to keep them across restarts.
RUN mkdir -p /app/WorkspaceStore /app/App_Data && chown -R $APP_UID /app/WorkspaceStore /app/App_Data
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
VOLUME ["/app/WorkspaceStore", "/app/App_Data"]
ENTRYPOINT ["dotnet", "AuBtechReviewAgent.dll"]
