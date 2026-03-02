// Sample file with known violations for integration tests

const apiKey = "sk-super-secret-key-1234";  // no-hardcoded-secret

function fetchData(url: string) {
    console.log("fetching", url);  // no-console-log
    return fetch(url);
}

// TODO fix this later  // no-todo-without-ticket

export { fetchData };
