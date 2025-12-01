# Natural Language Database Retriever

## Tech Stack
- .NET Core 8.0
- SQLite (Soccer Dataset)
- Semantic Kernel
- Angular 20 for a basic UI

## User Flow
User types their query in natural language, the system generates the relevant SQL query, retrieves the data and returns it to the user.

## Technical Details

### Schema Management
The database schema can be very large, so we first make a call to the LLM to get the relevant part of the schema for the query. This relevant schema is then passed to the LLM along with the query and chat history.

### Chat History
1. **Truncated History**: Keeps the previous 4 chats between user and bot
2. **Summarized Chat**: Maintains a summary of the conversation and runs after every 4 chats to summarize everything

### Session Management
Singleton pattern is used for the kernel, so we currently don't need to manage sessions and only one connection is maintained.

## Backend Flow
```
User Query
    ↓
Full Schema + Query + Chat History (×2)
    ↓
LLM → Relevant Schema
    ↓
Relevant Schema + Query
    ↓
LLM -> SQL Query
    ↓
Security Check
    ↓
Execute Query
    ↓
├─ Query Fails -> Retry once (send full schema to LLM for SQL)
└─ Query Success -> Response to User + Add to Memory



Since schemas can grow significantly in size, RAG (semantic search) may be implemented in the future to:
- Reduce LLM API call costs (using embedding generation models instead)
- Potentially reduce response time
