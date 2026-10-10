# Build from the repository root:  docker build -f web/PdfPecker.Web/Dockerfile -t pdfpecker-web .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/PdfPecker/ src/PdfPecker/
COPY web/PdfPecker.Web/ web/PdfPecker.Web/
COPY README.md .
RUN dotnet publish web/PdfPecker.Web -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "PdfPecker.Web.dll"]
