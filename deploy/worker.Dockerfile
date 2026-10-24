FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac
WORKDIR /opt/openetrm
COPY LICENSE ./LICENSE
COPY LICENSES/ ./LICENSES/
COPY worker/ ./worker/
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
USER 1654:1654
ENTRYPOINT ["dotnet", "/opt/openetrm/worker/OpenEtrm.Worker.dll"]
