;(function () {
  // Builds before the rename stored these under "opencode-"; carry a player's choice over once, in this profile.
  ;["theme-id", "color-scheme", "theme-css-light", "theme-css-dark"].forEach(function (name) {
    var old = localStorage.getItem("opencode-" + name)
    if (old !== null && localStorage.getItem("spectuner-" + name) === null) localStorage.setItem("spectuner-" + name, old)
  })

  var key = "spectuner-theme-id"
  var themeId = localStorage.getItem(key) || "oc-2"

  var scheme = localStorage.getItem("spectuner-color-scheme") || "system"
  var isDark = scheme === "dark" || (scheme === "system" && matchMedia("(prefers-color-scheme: dark)").matches)
  var mode = isDark ? "dark" : "light"

  document.documentElement.dataset.theme = themeId
  document.documentElement.dataset.colorScheme = mode
  document.documentElement.style.backgroundColor = isDark ? "#080808" : "#fafafa"

  // Update theme-color meta tag to match app color scheme
  var metas = document.querySelectorAll("meta[name='theme-color']")
  if (metas.length > 0) metas[0].setAttribute("content", isDark ? "#080808" : "#fafafa")

  if (themeId === "oc-2") return

  var css = localStorage.getItem("spectuner-theme-css-" + mode)
  if (css) {
    var style = document.createElement("style")
    style.id = "oc-theme-preload"
    style.textContent =
      ":root{color-scheme:" +
      mode +
      ";--text-mix-blend-mode:" +
      (isDark ? "plus-lighter" : "multiply") +
      ";" +
      css +
      "}"
    document.head.appendChild(style)
  }
})()
