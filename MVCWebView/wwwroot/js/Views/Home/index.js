async function reloadPage() {
    document.getElementById("treePanel").style.display = "block";
    fetchDependencyTree();
    loadSingleClassSvg();
}

async function loadEntireProject() {
    showSpinner();
    document.getElementById("treePanel").style.display = "none";
    const project = document.getElementById('projectInput').value;

    const response = await fetch(`/api/dependency/GetEntireProjectSVG?project=${encodeURIComponent(project)}`);
    if (response.ok) {
        const svg = await response.text();
        document.getElementById('svgOutput').innerHTML = svg;
    } else {
        document.getElementById('svgOutput').innerText = 'Failed to load SVG.';
    }
    hideSpinner();
    configureSVG();
}

async function loadAllControllers() {
    showSpinner();
    document.getElementById("treePanel").style.display = "none";
    const project = document.getElementById('projectInput').value;

    const response = await fetch(`/api/dependency/GetAllControllersSVG?project=${encodeURIComponent(project)}`);
    if (response.ok) {
        const svg = await response.text();
        document.getElementById('svgOutput').innerHTML = svg;
    } else {
        document.getElementById('svgOutput').innerText = 'Failed to load SVG.';
    }
    hideSpinner();
    configureSVG();
}

async function fetchDependencyTree() {
    const className = document.getElementById("classInput").value;
    const project = document.getElementById("projectInput").value;

    const response = await fetch(`/api/Dependency/GetDependencyTreeText?className=${encodeURIComponent(className)}&project=${encodeURIComponent(project)}`);

    if (!response.ok) {
        const errorText = await response.text();
        document.getElementById("treeOutput").textContent = "Error: " + errorText;
        return;
    }

    const html = await response.text();
    document.getElementById("treeOutput").innerHTML = html;
}


async function loadSingleClassSvg() {
    showSpinner();
    const className = document.getElementById('classInput').value;
    const project = document.getElementById('projectInput').value;

    const response = await fetch(`/api/dependency/GetSvg?className=${encodeURIComponent(className)}&project=${encodeURIComponent(project)}`);
    if (response.ok) {
        const svg = await response.text();
        document.getElementById('svgOutput').innerHTML = svg;
    } else {
        document.getElementById('svgOutput').innerText = 'Failed to load SVG.';
    }
    hideSpinner();

    configureSVG(className);
}

function configureSVG(className) {
    const svg = document.querySelector("#svgOutput svg");
    if (svg) {
        svg.removeAttribute("width");
        svg.removeAttribute("height");
        svg.setAttribute("width", "100%");
        svg.setAttribute("height", "100%");
        svg.setAttribute("preserveAspectRatio", "xMidYMid meet");
        svg.style.maxWidth = "100%";
        svg.style.height = "100%";

        // Optional if missing:
        if (!svg.hasAttribute("viewBox")) {
            const bbox = svg.getBBox();
            svg.setAttribute("viewBox", `0 0 ${bbox.width} ${bbox.height}`);
        }

        svgPanZoom(svg, {
            zoomEnabled: true,
            controlIconsEnabled: true,
            fit: true,
            center: true,
            minZoom: 0.2,
            maxZoom: 10,
            contain: true,
        });

        attachClickHandlers();
        if(className) centerSVGOnSelectedClass(className);
    }
}
function centerSVGOnSelectedClass(className) {
    const node = findNodeByTitle(className);
    if (!node) return;

    const svg = document.querySelector("#svgOutput svg");
    if (!svg || !window.panZoomInstance) return;

    const bbox = node.getBBox();
    const viewCenter = {
        x: svg.clientWidth / 2,
        y: svg.clientHeight / 2
    };

    const zoom = window.panZoomInstance.getZoom();
    window.panZoomInstance.pan({
        x: viewCenter.x - (bbox.x + bbox.width / 2) * zoom,
        y: viewCenter.y - (bbox.y + bbox.height / 2) * zoom
    });
}

function findNodeByTitle(className) {
    const svg = document.querySelector("#svgOutput svg");
    const nodes = svg.querySelectorAll("g.node");

    for (const node of nodes) {
        const title = node.querySelector("title");
        if (title?.textContent.trim() === className) {
            return node;
        }
    }
    return null;
}

function attachClickHandlers() {
    const svg = document.querySelector("#svgOutput svg");
    if (!svg) return;

    const nodes = svg.querySelectorAll('g.node');

    nodes.forEach(node => {
        const titleElement = node.querySelector("title");
        if (!titleElement) return;

        const className = titleElement.textContent?.trim();
        if (!className) return;

        node.style.cursor = "pointer";
        node.addEventListener("click", () => {
            console.log("Clicked node:", className);
            document.getElementById("classInput").value = className;
            reloadPage();
        });
    });
}
function showSpinner() {
    document.getElementById("loadingSpinner").style.display = "flex";
}

function hideSpinner() {
    document.getElementById("loadingSpinner").style.display = "none";
}