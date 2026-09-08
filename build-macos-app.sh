#!/bin/bash
# build-macos-app.sh
# Compila S1Utility y lo empaqueta como S1Utility.app para macOS.
#
# Uso:
#   chmod +x build-macos-app.sh
#   ./build-macos-app.sh
#
# Ejecutar desde la carpeta raíz del repo (la que contiene la carpeta S1Utility/ con el .csproj)

set -e  # detener el script si algo falla

APP_NAME="S1Utility"
BUNDLE_NAME="${APP_NAME}.app"
PROJECT_PATH="S1Utility/S1Utility.csproj"
PUBLISH_DIR="./publish"

# Detecta automáticamente si es Apple Silicon o Intel
ARCH=$(uname -m)
if [ "$ARCH" = "arm64" ]; then
    RID="osx-arm64"
else
    RID="osx-x64"
fi

echo "Arquitectura detectada: $ARCH -> usando RID: $RID"

# 1. Limpia publicaciones anteriores
rm -rf "$PUBLISH_DIR"
rm -rf "$BUNDLE_NAME"

# 2. Publica el proyecto
echo "Compilando y publicando..."
dotnet publish "$PROJECT_PATH" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o "$PUBLISH_DIR"

# 3. Crea la estructura del bundle .app
echo "Creando estructura del bundle..."
mkdir -p "$BUNDLE_NAME/Contents/MacOS"
mkdir -p "$BUNDLE_NAME/Contents/Resources"

# 4. Copia los binarios publicados dentro del bundle
cp -r "$PUBLISH_DIR"/* "$BUNDLE_NAME/Contents/MacOS/"

# 5. Genera el Info.plist
cat > "$BUNDLE_NAME/Contents/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key>
  <string>${APP_NAME}</string>
  <key>CFBundleIdentifier</key>
  <string>com.denzlobin.s1utility</string>
  <key>CFBundleName</key>
  <string>S1 Utility</string>
  <key>CFBundleVersion</key>
  <string>1.0</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>1.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
EOF

# 6. Permisos de ejecución
chmod +x "$BUNDLE_NAME/Contents/MacOS/${APP_NAME}"

# 7. Quita la cuarentena de Gatekeeper (la app no está firmada por Apple)
xattr -cr "$BUNDLE_NAME"

echo ""
echo "Listo. Se ha creado ${BUNDLE_NAME} en esta carpeta."
echo "Puedes abrirlo con doble clic o arrastrarlo a /Applications."
