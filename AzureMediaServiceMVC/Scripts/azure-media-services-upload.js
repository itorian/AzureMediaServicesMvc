
var maxRetries = 3;
var blockLength = 10576; //1048576
var numberOfBlocks = 1;
var currentChunk = 1;
var retryAfterSeconds = 3;

$(document).ready(function () {
    $(document).on("click", "#fileUpload", beginUpload);
    $("#progressBar").progressbar(0);
});

var beginUpload = function () {
    var fileControl = document.getElementById("selectFile");
    if (fileControl.files.length > 0) {
        for (var i = 0; i < fileControl.files.length; i++) {
            uploadMetaData(fileControl.files[i], i);
        }
    }
}

var uploadMetaData = function (file, index) {
    var size = file.size;
    numberOfBlocks = Math.ceil(file.size / blockLength);
    var name = file.name;
    currentChunk = 1;

    $.ajax({
        type: "POST",
        async: false,
        url: "/Video/SetMetadata?blocksCount=" + numberOfBlocks + "&fileName=" + name + "&fileSize=" + size,
    }).done(function (state) {
        if (state === true) {
            $("#fileUpload").hide();
            displayStatusMessage("On your mark, get set, go....");
            sendFile(file, blockLength);
        }
    }).fail(function () {
        $("#fileUpload").show()
        displayStatusMessage("Failed to send MetaData");
    });
}

var sendFile = function (file, chunkSize) {
    var start = 0, end = Math.min(chunkSize, file.size), retryCount = 0, sendNextChunk, fileChunk;

    sendNextChunk = function () {
        fileChunk = new FormData();

        if (file.slice) {
            fileChunk.append('Slice', file.slice(start, end));
        }
        else if (file.webkitSlice) {
            fileChunk.append('Slice', file.webkitSlice(start, end));
        }
        else if (file.mozSlice) {
            fileChunk.append('Slice', file.mozSlice(start, end));
        }
        else {
            displayStatusMessage(operationType.UNSUPPORTED_BROWSER);
            return;
        }
        jqxhr = $.ajax({
            async: true,
            url: ('/Video/UploadChunk?id=' + currentChunk),
            data: fileChunk,
            cache: false,
            contentType: false,
            processData: false,
            type: 'POST'
        }).fail(function (request, error) {
            if (error !== 'abort' && retryCount < maxRetries) {
                ++retryCount;
                setTimeout(sendNextChunk, retryAfterSeconds * 1000);
            }

            if (error === 'abort') {
                displayStatusMessage("Aborted");
            }
            else {
                if (retryCount === maxRetries) {
                    displayStatusMessage("Upload timed out.");
                    resetControls();
                    uploader = null;
                }
                else {
                    displayStatusMessage("Resuming Upload.");
                }
            }

            return;
        }).done(function (notice) {
            if (notice.error || notice.isLastBlock) {
                displayStatusMessage(notice.message);
                if (notice.isLastBlock) {
                    encodeFile(notice.assetId);
                    $("#assetId").val(notice.assetId);
                }
                return;
            }
            ++currentChunk;
            start = (currentChunk - 1) * blockLength;
            end = Math.min(currentChunk * blockLength, file.size);
            retryCount = 0;
            updateProgress();
            if (currentChunk <= numberOfBlocks) {
                sendNextChunk();
            }
        });
    }
    sendNextChunk();
}

var encodeFile = function (assetId) {
    $.ajax({
        type: "POST",
        async: false,
        url: "/Video/EncodeToAdaptiveBitrateMP4s?assetId=" + assetId,
    }).done(function (state) {
        if (!state.error) {
            displayStatusMessage(state.message);
            console.log("Asset Id: " + state.assetId);
            console.log("Job Id: " + state.jobId);
            console.log("Locator: " + state.locator);
            console.log("Encrypted content or not: " + state.encrypted);
            console.log("Token: " + state.token);
            getJobState(state.jobId, state.assetId);
        }
    }).fail(function (state) {
        if (state.error) {
            displayStatusMessage(state.message);
        }
    });
}

// Smells buggy code, needs improvement
var getJobState = function (jobId, assetId) {
    var url = "/Video/GetEncodingJobStatus";
    $.get(url, { jobId: jobId })
    .done(function (data) {
        if (!data.error) {
            if (data.state != "Finished") {
                displayStatusMessage("Job State : " + data.state);
                setTimeout(function () { getJobState(jobId, assetId); }, 5000);
            }
            if (data.state == "Finished") {
                displayStatusMessage("Encoding job completed");
            }
        }
        else {
            displayStatusMessage(data.message);
        }

    })
    .fail(function () {
        displayStatusMessage("Encoding job status check error");
    });
}

var displayStatusMessage = function (message) {
    $("#statusMessage").text(message);
    console.log(message);
}

var updateProgress = function () {
    var progress = currentChunk / numberOfBlocks * 100;
    if (progress <= 100) {
        $("#progressBar").progressbar("option", "value", parseInt(progress));
        displayStatusMessage("Please wait, uploaded " + parseInt(progress) + "%");
    }
}