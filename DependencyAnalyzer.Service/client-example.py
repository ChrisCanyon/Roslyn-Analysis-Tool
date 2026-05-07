#!/usr/bin/env python3
"""
Python client for DependencyAnalyzer Service

Usage:
    python client-example.py load "C:\\path\\to\\YourSolution.sln"
    python client-example.py leaks --project YourProject
    python client-example.py node SomeClass
    python client-example.py search SomePattern
"""

import requests
import json
import sys
from typing import Optional, Dict, Any

class DependencyAnalyzerClient:
    def __init__(self, base_url: str = "http://localhost:5123"):
        self.base_url = base_url
        self.session = requests.Session()

    def status(self) -> Dict[str, Any]:
        """Check service status"""
        response = self.session.get(f"{self.base_url}/status")
        response.raise_for_status()
        return response.json()

    def load_solution(self, solution_path: str, force_reload: bool = False) -> Dict[str, Any]:
        """Load a solution"""
        data = {
            "solutionPath": solution_path,
            "forceReload": force_reload
        }
        response = self.session.post(f"{self.base_url}/load", json=data)
        response.raise_for_status()
        return response.json()

    def get_transient_leaks(self, project: Optional[str] = None) -> list:
        """Find transient memory leaks"""
        params = {"project": project} if project else {}
        response = self.session.get(f"{self.base_url}/transient-leaks", params=params)
        response.raise_for_status()
        return response.json()

    def get_node(self, class_name: str, project: Optional[str] = None) -> Dict[str, Any]:
        """Get information about a specific node"""
        params = {"project": project} if project else {}
        response = self.session.get(f"{self.base_url}/node/{class_name}", params=params)
        response.raise_for_status()
        return response.json()

    def search(self, pattern: str, search_type: Optional[str] = None) -> Any:
        """Search for classes, methods, or dependencies"""
        params = {"pattern": pattern}
        if search_type:
            params["searchType"] = search_type
        response = self.session.get(f"{self.base_url}/search", params=params)
        response.raise_for_status()
        return response.json()

    def get_captive_dependencies(self, class_name: str) -> list:
        """Find captive dependencies for a class"""
        response = self.session.get(f"{self.base_url}/captive-dependencies/{class_name}")
        response.raise_for_status()
        return response.json()

    def query(self, query_type: str, parameters: Dict[str, Any]) -> Any:
        """Execute a custom query"""
        data = {
            "queryType": query_type,
            "parameters": parameters
        }
        response = self.session.post(f"{self.base_url}/query", json=data)
        response.raise_for_status()
        return response.json()


def main():
    client = DependencyAnalyzerClient()

    if len(sys.argv) < 2:
        print("Usage: python client-example.py <command> [args]")
        print("Commands: status, load, leaks, node, search, captive")
        return

    command = sys.argv[1]

    try:
        if command == "status":
            result = client.status()
            print(json.dumps(result, indent=2))

        elif command == "load" and len(sys.argv) > 2:
            solution_path = sys.argv[2]
            print(f"Loading solution: {solution_path}")
            result = client.load_solution(solution_path)
            print(json.dumps(result, indent=2))

        elif command == "leaks":
            project = sys.argv[2] if len(sys.argv) > 2 else None
            leaks = client.get_transient_leaks(project)
            print(f"Found {len(leaks)} transient leaks:")
            for leak in leaks:
                print(f"  - {leak['Type']} in {leak['Project']}")
                print(f"    Has Release: {leak['HasRelease']}")
                if not leak['HasRelease']:
                    print("    ⚠️  MEMORY LEAK!")

        elif command == "node" and len(sys.argv) > 2:
            class_name = sys.argv[2]
            node = client.get_node(class_name)
            print(json.dumps(node, indent=2))

        elif command == "search" and len(sys.argv) > 2:
            pattern = sys.argv[2]
            results = client.search(pattern)
            print(json.dumps(results, indent=2))

        elif command == "captive" and len(sys.argv) > 2:
            class_name = sys.argv[2]
            captives = client.get_captive_dependencies(class_name)
            print(f"Captive dependencies for {class_name}:")
            for captive in captives:
                print(f"  - {captive['Consumer']} ({captive['ConsumerLifetime']}) holds {captive['Target']} ({captive['TargetLifetime']})")

        else:
            print(f"Unknown command or missing arguments: {command}")

    except requests.exceptions.RequestException as e:
        print(f"Error: {e}")
        if hasattr(e, 'response') and e.response is not None:
            print(f"Response: {e.response.text}")


if __name__ == "__main__":
    main()