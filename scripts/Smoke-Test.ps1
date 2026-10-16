param([string]$BaseUrl = 'http://localhost:5063')
$ErrorActionPreference = 'Stop'

function Send-Request($Method, $Path, $Body, $ExpectedStatus) {
    $parameters = @{
        Uri = "$BaseUrl$Path"
        Method = $Method
        SkipHttpErrorCheck = $true
    }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = ConvertTo-Json $Body
    }
    $response = Invoke-WebRequest @parameters
    $content = if ($response.Content -is [byte[]]) {
        [System.Text.Encoding]::UTF8.GetString($response.Content)
    } else { $response.Content }
    if ([int]$response.StatusCode -ne $ExpectedStatus) {
        throw "$Method $Path expected $ExpectedStatus, received $($response.StatusCode): $content"
    }
    if ($content) { return $content | ConvertFrom-Json }
}

$category = $null
$supplier = $null
$product = $null
try {
    $category = Send-Request POST '/api/categories' @{ name = 'Smoke category'; description = 'Temporary' } 201
    $supplier = Send-Request POST '/api/suppliers' @{ name = 'Smoke supplier'; email = 'smoke@example.com' } 201
    $body = @{ name = 'Smoke product'; price = 12.50; stockQuantity = 5; categoryId = $category.id; supplierId = $supplier.id }
    $product = Send-Request POST '/api/products' $body 201

    foreach ($entity in @($category, $supplier, $product)) {
        $parsedId = [guid]::Parse($entity.id)
        if ($parsedId -eq [guid]::Empty) { throw 'Entity ID must be a nonempty GUID.' }
    }

    foreach ($resource in @('categories', 'suppliers', 'products')) {
        $null = Send-Request GET "/api/$resource" $null 200
    }
    $null = Send-Request GET "/api/categories/$($category.id)" $null 200
    $null = Send-Request GET "/api/suppliers/$($supplier.id)" $null 200
    $read = Send-Request GET "/api/products/$($product.id)" $null 200
    if ($read.categoryName -ne 'Smoke category' -or $read.supplierName -ne 'Smoke supplier') {
        throw 'Product relationship names were not returned correctly.'
    }

    $null = Send-Request PUT "/api/categories/$($category.id)" @{ name = 'Updated category'; description = 'Changed' } 204
    $null = Send-Request PUT "/api/suppliers/$($supplier.id)" @{ name = 'Updated supplier'; email = 'updated@example.com' } 204
    $body.name = 'Updated product'
    $body.price = 25.75
    $null = Send-Request PUT "/api/products/$($product.id)" $body 204
    $read = Send-Request GET "/api/products/$($product.id)" $null 200
    if ($read.name -ne 'Updated product' -or $read.price -ne 25.75 -or
        $read.categoryName -ne 'Updated category' -or $read.supplierName -ne 'Updated supplier') {
        throw 'Updates were not persisted correctly.'
    }

    $null = Send-Request DELETE "/api/categories/$($category.id)" $null 409
    $null = Send-Request DELETE "/api/suppliers/$($supplier.id)" $null 409
    $body.price = -1
    $null = Send-Request POST '/api/products' $body 400
    $body.price = 1
    $body.categoryId = [guid]::NewGuid().ToString()
    $null = Send-Request POST '/api/products' $body 400
    $body.categoryId = [guid]::Empty.ToString()
    $null = Send-Request POST '/api/products' $body 400
    $body.categoryId = $category.id
    $body.supplierId = [guid]::Empty.ToString()
    $null = Send-Request POST '/api/products' $body 400
    $null = Send-Request POST '/api/categories' @{ name = ' ' } 400
    $null = Send-Request POST '/api/suppliers' @{ name = 'Invalid'; email = 'invalid-email' } 400

    $null = Send-Request DELETE "/api/products/$($product.id)" $null 204
    $null = Send-Request GET "/api/products/$($product.id)" $null 404
    $product = $null
    $null = Send-Request DELETE "/api/categories/$($category.id)" $null 204
    $null = Send-Request GET "/api/categories/$($category.id)" $null 404
    $category = $null
    $null = Send-Request DELETE "/api/suppliers/$($supplier.id)" $null 204
    $null = Send-Request GET "/api/suppliers/$($supplier.id)" $null 404
    $supplier = $null
    Write-Output 'PASS: CRUD for all entities, persistence, relationships, validation, delete conflicts, and 404 responses.'
}
finally {
    # Only remove records created by this run, including after a failed assertion.
    if ($product) { $null = Send-Request DELETE "/api/products/$($product.id)" $null 204 }
    if ($category) { $null = Send-Request DELETE "/api/categories/$($category.id)" $null 204 }
    if ($supplier) { $null = Send-Request DELETE "/api/suppliers/$($supplier.id)" $null 204 }
}
