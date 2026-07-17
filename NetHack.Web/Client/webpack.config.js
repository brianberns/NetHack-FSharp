var path = require("path");

/*
 * The mode arrives on the command line: the dev server runs plain `webpack
 * serve`, and the build passes `--mode production`. Only `mode` is set that
 * way, so anything else that ought to differ between the two has to be decided
 * here -- notably devtool, as eval-source-map inlines every module's source
 * into the bundle and would otherwise ship to production.
 */
module.exports = (env, argv) => {
    var isProduction = argv.mode === "production";

    return {
        mode: isProduction ? "production" : "development",
        // a separate .map file in production: the browser fetches it only when
        // devtools are open, so it costs the visitor nothing
        devtool: isProduction ? "source-map" : "eval-source-map",
        entry: "./src/App.fs.js",
        output: {
            path: path.join(__dirname, "./public"),
            filename: "bundle.js",
        },
        devServer: {
            static: "./public",
            port: 8081,
            proxy: [
                {
                    context: ['/NetHackWeb/INetHackApi/**'],
                    target: "http://127.0.0.1:5000/",
                    changeOrigin: true
                }
            ]
        },
        resolve: {
            alias: {
                jquery: "jquery/src/jquery"
            }
        },
        module: {
            rules: [
                {
                    test: /\.js$/,
                    enforce: "pre",
                    use: ["source-map-loader"],
                },
                {
                    test: /\.css$/i,
                    use: ['style-loader', 'css-loader'],
                },
                {
                    test: /\.(png|svg|jpg|jpeg|gif)$/i,
                    type: 'asset/resource',
                }
            ]
        }
    };
};
