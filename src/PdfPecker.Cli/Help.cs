namespace PdfPecker.Cli;

internal static class Help
{
    public const string Overview = """
        pdfpecker - HTML and Markdown to PDF, and tools for PDFs. No browser, no scripts, no network.

        Usage:
          pdfpecker <command> [arguments] [options]
          pdfpecker [wkhtmltopdf options] <input.html>... <output.pdf>

        Make PDFs:
          convert     HTML or Markdown file to PDF (also from standard input with -)
          watch       convert again every time the file or the files it uses change

        Change PDFs:
          merge       join PDFs, or pages of them, into one
          split       cut a PDF into files of N pages or of the ranges you name
          extract     keep some pages        remove     drop some pages
          rotate      turn pages by 90, 180 or 270 degrees
          stamp       lay another PDF or an HTML page over (or under) every page
          compact     make a PDF smaller      linearize  Fast Web View
          sanitize    remove scripts and actions that launch programs

        Sign and protect:
          sign        sign with a .pfx certificate     verify   check signatures
          encrypt     AES-256 password and permissions  decrypt  remove the password

        Read PDFs:
          to-image    PNG, JPEG, WebP or TIFF          to-html  one HTML file that looks like the PDF
          text        extract the text                 info     pages, signatures, encryption

        Use '-' for standard input or output. Run 'pdfpecker help <command>' for a command's options.
        A call without a command word works like wkhtmltopdf: pdfpecker -s A4 -T 15mm in.html out.pdf

        """;

    public static string ForCommand(string command) => command switch
    {
        "convert" or "watch" => $"""
            pdfpecker {command} <input.html|input.md>... [output.pdf] [options]

            {(command == "watch" ? "Converts once, then again each time the input, its folder or the style sheet changes. A failed\nconversion is shown and watching goes on. Press Ctrl+C to stop.\n" : "")}
            With several inputs the last argument is the output and must end in .pdf; the inputs are joined.
            Without an output the PDF is written next to the input. Markdown is recognised by the extension.

            Page:        -s, --page-size A4|Letter|B5|100x150mm   --page-width, --page-height   -O, --orientation Landscape|Portrait
                         --landscape   -T/-R/-B/-L, --margin-top/-right/-bottom/-left   --margin (all four)   (unit mm, cm, in, pt or px)
                         --zoom 0.8   --disable-smart-shrinking
            Text:        --header-left/-center/-right TEXT   --footer-left/-center/-right TEXT  with [page] [topage] [date] [time] [title]
                         --header-font-size, --footer-font-size, --header-font-name, --footer-font-name
            Document:    --title --author --subject --keywords --lang --creation-date   --no-outline
            Styling:     --user-style-sheet FILE (--css)   --font FILE   --font-dir DIR   --system-fonts   --base-dir DIR
            Files:       --enable-local-file-access (--allow-file-access) lets the document read files outside its folder
            Output:      --pdfa 2b|2u|3b|3u|4|4f   --accessibility tagged|ua1|ua2   --user-password, --owner-password
                         --linearize   --no-pdf-compression   -o, --output FILE   -q, --quiet   --markdown / --html

            wkhtmltopdf options that need a browser engine, scripts or the network (--javascript-delay, --cookie,
            --header-html, --grayscale ...) are accepted and ignored with a warning.

            """,
        "merge" => "pdfpecker merge <a.pdf> <b.pdf>[:pages]... -o <output.pdf> [--title T] [--author A] [--subject S] [--keywords K] [--no-bookmarks] [--password PW]\n  A page selection follows a colon: report.pdf:1-3,7\n",
        "split" => "pdfpecker split <input.pdf> [--every N | --range 1-3 --range 4-6] [--out-dir DIR] [--prefix NAME] [--password PW]\n  Writes NAME-001.pdf, NAME-002.pdf ... next to the input unless --out-dir is given.\n",
        "extract" => "pdfpecker extract <input.pdf> <output.pdf> --pages 1-3,5 [--password PW]\n",
        "remove" => "pdfpecker remove <input.pdf> <output.pdf> --pages 2,4 [--password PW]\n",
        "rotate" => "pdfpecker rotate <input.pdf> <output.pdf> --degrees 90 [--pages 1-3] [--password PW]\n",
        "stamp" => "pdfpecker stamp <input.pdf> <stamp.pdf|stamp.html> <output.pdf> [--underlay] [--opacity 0.5] [--scale 1] [--position center|top-left|...] [--offset-x N] [--offset-y N] [--pages 1,3] [--stamp-page N]\n",
        "compact" => "pdfpecker compact <input.pdf> <output.pdf> [--image-quality 1-100] [--max-dpi N] [--remove-metadata]\n",
        "linearize" => "pdfpecker linearize <input.pdf> <output.pdf>\n",
        "sanitize" => "pdfpecker sanitize <input.pdf> <output.pdf>\npdfpecker sanitize <input.pdf> --check      (exit code 1 when the PDF has active content)\n",
        "sign" => "pdfpecker sign <input.pdf> <output.pdf> --cert <certificate.pfx> [--cert-password PW | --cert-password-env VAR]\n  [--reason T] [--location T] [--contact T] [--name T] [--field NAME] [--page N --rect x,y,width,height] [--text T]\n  [--certify none|no-changes|forms|annotations] [--format pades|pkcs7] [--hash sha256|sha384|sha512] [--timestamp-url URL]\n  Without --cert-password the PDFPECKER_CERT_PASSWORD environment variable is used.\n",
        "verify" => "pdfpecker verify <input.pdf> [--trusted-root ca.cer]... [--allow-sha1] [--check-revocation] [--password PW]\n  Exit code 0 when every signature is valid, 1 otherwise (also when there are none).\n",
        "encrypt" => "pdfpecker encrypt <input.pdf> <output.pdf> --user-password PW [--owner-password PW] [--allow print,copy,modify,annotate,forms,accessibility,assemble|all]\n  --user-password-env and --owner-password-env read the password from an environment variable.\n",
        "decrypt" => "pdfpecker decrypt <input.pdf> <output.pdf> --password PW\n",
        "to-image" => "pdfpecker to-image <input.pdf> [output-directory] [--format png|jpeg|webp|tiff] [--dpi 150] [--quality 1-100] [--width PX] [--height PX] [--pages 1-3] [--transparent] [--prefix NAME]\n  Writes NAME-001.png ... (one TIFF file for tiff).\n",
        "to-html" => "pdfpecker to-html <input.pdf> [output.html] [--mode visual|structured] [--pages 1-3]\n",
        "text" => "pdfpecker text <input.pdf> [output.txt] [--pages 1-3]\n  Without an output the text goes to standard output.\n",
        "info" => "pdfpecker info <input.pdf> [--password PW]\n",
        _ => Overview,
    };
}
