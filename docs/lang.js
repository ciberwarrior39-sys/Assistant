// Language: Bulgarian for Bulgarian browsers, English otherwise; the choice is remembered on this browser.
(function () {
  var saved = null;
  try { saved = localStorage.getItem("lang"); } catch (e) {}
  var lang = saved || ((navigator.language || "").toLowerCase().indexOf("bg") === 0 ? "bg" : "en");
  var root = document.documentElement;
  function apply(next) {
    lang = next;
    root.setAttribute("data-lang", next);
    root.lang = next;
    document.querySelectorAll(".langs button").forEach(function (b) {
      b.setAttribute("aria-pressed", String(b.getAttribute("data-set") === next));
    });
    var title = root.getAttribute("data-title-" + next);
    if (title) document.title = title;
  }
  document.querySelectorAll(".langs button").forEach(function (b) {
    b.addEventListener("click", function () {
      apply(b.getAttribute("data-set"));
      try { localStorage.setItem("lang", lang); } catch (e) {}
    });
  });
  apply(lang);
})();
