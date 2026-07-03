//38x20
//if (!json.RootElement.TryGetProperty("valores", out var arr) ||
//    arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
//{
//    res.StatusCode = 400;
//    await Write(res, new { error = "Falta arreglo 'valores' con nombre y codigo_barra" });
//    return;
//}

//var sb = new StringBuilder();
////38X20
//foreach (var item in arr.EnumerateArray())
//{
//    // --- datos base ---------------------------------------------------------
//    var nombre      = item.GetProperty("nombre").GetString()        ?? "";
//    var codigo      = item.GetProperty("codigo_barra").GetString()  ?? "";
//    var precio      = item.GetProperty("precio").GetString()        ?? "";
//    var usePrecioEl = item.GetProperty("use_precio").GetString()    ?? "false";

//    // --- valida si debe mostrar precio -------------------------------------
//    var mostrarPrecio = bool.TryParse(usePrecioEl, out var flag) && flag;

//    // —–– limita largo del precio para que nunca desborde (≈16 carac. máx.) –
//    if (precio.Length > 16) precio = precio[..16];

//    // --- plantilla ZPL ------------------------------------------------------
//    sb.Append("^XA")// inicio etiqueta
//    .Append("^PW300^LH0,0")               // 300 dots (≈37,5 mm de ancho útil)
//    .Append("^BY1,2,30")                  // ancho barras, ratio, alto 30
//    .Append("^FO20,10^BCN,30,N,N,N")      // barcode sin texto auto
//    .Append("^FD").Append(codigo).Append("^FS")
//    .Append("^FO20,45")                   // nombre bajo el código de barras
//    .Append("^FB260,3,0,L,0")             // bloque 260 px, máx. 3 líneas, alineado izq.
//    .Append("^A0N,16,16^FD").Append(nombre).Append("^FS");

//    // --- precio (opcional, centrado) ---------------------------------------
//    if (mostrarPrecio)
//    {
//        sb.Append("^FO20,70")               // posición vertical
//        .Append("^FB260,1,0,C,0")         // bloque 260 px, 1 línea, alineado CENTRO
//        .Append("^A0N,16,16^FD").Append(precio).Append("^FS");
//    }

//    sb.Append("^XZ");   // fin etiqueta
//}


//_queue.Add(new PrintJob(JobKind.Zpl, sb.ToString()));
//await Write(res, new { status = "queued" });
//return;


//50X25
//foreach (var item in arr.EnumerateArray())
//{
//    var nombre = item.GetProperty("nombre").GetString() ?? "";
//    var codigo = item.GetProperty("codigo_barra").GetString() ?? "";

//    sb.Append("^XA")
//    .Append("^PW300^LH0,0") // Ancho máximo ≈ 38mm
//    .Append("^BY2,2,30")     // Código de barras más bajo
//    .Append("^FO20,10^BCN,30,Y,N,N^FD").Append(codigo).Append("^FS") // Código de barras
//    .Append("^FO20,50^A0N,16,16^FD").Append(nombre).Append("^FS")    // Nombre del producto
//    .Append("^XZ");
//}
//_queue.Add(new PrintJob(JobKind.Zpl, sb.ToString()));
//await Write(res, new { status = "queued" });
//return;