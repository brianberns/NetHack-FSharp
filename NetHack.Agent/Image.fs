namespace NetHack.Agent

open System
open System.IO
open System.Numerics

open SixLabors.Fonts
open SixLabors.ImageSharp
open SixLabors.ImageSharp.Drawing.Processing
open SixLabors.ImageSharp.Formats.Png
open SixLabors.ImageSharp.PixelFormats
open SixLabors.ImageSharp.Processing

open NetHack.Api

module Image =

    /// Renders the given dungeon map to a PNG in memory.
    let getMap (observation : Observation) =

        let grid = observation.Rows
        assert(grid.Length > 0)
        let numRows = grid.Length
        let numCols = grid |> Seq.map _.Length |> Seq.max

        let font =
            SystemFonts.Families
                |> Seq.tryFind (fun family ->
                    match family.Name with
                        | "Consolas" | "Courier New" -> true
                        | _ -> false)
                |> Option.defaultValue (
                    Seq.head SystemFonts.Families)
                |> _.CreateFont(40f)

        let textOptions = TextOptions(font)

        let cellWidth =
            TextMeasurer
                .MeasureAdvance("W", textOptions)
                .Width
                |> ceil
                |> int
        let cellHeight =
            float32 font.FontMetrics.VerticalMetrics.LineHeight
                * (font.Size / float32 font.FontMetrics.UnitsPerEm)
                |> ceil
                |> int

        let imageWidth = numCols * cellWidth
        let imageHeight = numRows * cellHeight

        let text = String.concat "\n" grid
        let bounds = TextMeasurer.MeasureBounds(text, textOptions)
        let originY =
            (float32 imageHeight - bounds.Height) / 2f - bounds.Y

        use image = new Image<Rgba32>(imageWidth, imageHeight)

        image.Mutate(fun ctx ->
            let options =
                RichTextOptions(font, Origin = Vector2(0f, originY))
            ctx
                .SetGraphicsOptions(GraphicsOptions(Antialias = false))
                .Fill(Color.Black)
                .DrawText(options, text, Color.White)
                |> ignore)

        let encoder =
            PngEncoder(
                ColorType = Nullable(PngColorType.Grayscale),
                BitDepth = Nullable(PngBitDepth.Bit1))
        use stream = new MemoryStream()
        image.Save(stream, encoder)
        stream.ToArray()
