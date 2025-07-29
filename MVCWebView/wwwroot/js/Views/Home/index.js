async function reloadPage() {
    document.getElementById("treePanel").style.display = "block";
    fetchDependencyTree();
    loadSingleClassSvg();
} 

document.addEventListener("DOMContentLoaded", () => {
    const input = document.getElementById("classInput");
    console.log("adding listener");
    input.addEventListener("input", (event) => {
        el = event.target;
        const length = Math.max(el.value.length, 25);
        el.style.setProperty("width", `${length}ch`, "important");
    });
});

async function loadPrevious() {
    if (historyStack.length < 2) {
        console.warn("No previous view.");
        return;
    }

    const currentView = historyStack.pop();
    const lastView = historyStack.pop();

    if (!lastView.project) {
        console.warn("Missing class name for SINGLE_CLASS view.");
        return;
    }
    document.getElementById("projectInput").value = lastView.project;

    switch (lastView.type) {
        case ViewType.CONTROLLER:
            await loadAllControllers();
            break;

        case ViewType.ENTIRE_PROJECT:
            await loadEntireProject();
            break;

        case ViewType.SINGLE_CLASS:
            if (!lastView.className) {
                console.warn("Missing class name for SINGLE_CLASS view.");
                return;
            }
            document.getElementById("classInput").value = lastView.className;
            await reloadPage(lastView.project, lastView.className);
            break;

        default:
            console.warn("Unknown view type:", lastView.type);
    }
}

const ViewType = {
    CONTROLLER: "Controller",
    ENTIRE_PROJECT: "EntireProject",
    SINGLE_CLASS: "SingleClass"
};

const historyStack = [];

function createViewContext(type, project, className = null) {
    return {
        type,               // one of ViewType
        project,            // string
        className           // string or null (only for SINGLE_CLASS)
    };
}

async function loadEntireProject() {
    showSpinner();
    document.getElementById("treePanel").style.display = "none";
    const project = document.getElementById('projectInput').value;

    const response = await fetch(`/api/dependency/GetEntireProjectSVG?project=${encodeURIComponent(project)}`);
    if (response.ok) {
        const svg = await response.text();
        document.getElementById('svgOutput').innerHTML = svg;
        historyStack.push(createViewContext(ViewType.ENTIRE_PROJECT, project))
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
        historyStack.push(createViewContext(ViewType.CONTROLLER, project))
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
    historyStack.push(createViewContext(ViewType.SINGLE_CLASS,project, className))
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