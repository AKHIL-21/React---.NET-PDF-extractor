# PDF Form Extractor

React + .NET app for uploading a fillable PDF, extracting AcroForm field values on the API, and placing those values into an editable React form.

## Run the API

```bash
dotnet run --project API/API.csproj --launch-profile http
```

The API runs at `http://localhost:5057`.

## Run the React app

```bash
npm run dev
```

Vite proxies `/api` requests to the .NET API.

## Build

```bash
dotnet build API/API.csproj --no-restore
npm run build
```

## Endpoint

`POST /api/pdf/extract-fields`

Send multipart form data with a `file` PDF field. The response shape is:

```json
{
  "fileName": "form.pdf",
  "fieldCount": 2,
  "fields": {
    "FirstName": "Akhil",
    "LastName": "Example"
  }
}
```
